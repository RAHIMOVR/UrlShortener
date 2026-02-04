using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Database: urls.db in the project folder
var dbPath = Path.Combine(app.Environment.ContentRootPath, "urls.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

InitializeDatabase(connectionString);

const int MaxCodeAttempts = 10;
const int MinExpiryDays = 1;
const int MaxExpiryDays = 365;
const int DefaultPageSize = 20;
const int MaxPageSize = 100;

// Aliases that would clash with existing routes or files
var reservedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "api",
    "index.html",
    "favicon.ico"
};

app.MapPost("/api/links", (CreateLinkRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "The 'url' field is required." });

    var url = request.Url.Trim();

    if (!IsValidHttpUrl(url))
        return Results.BadRequest(new { error = "The URL must be an absolute http or https URL." });

    // An empty alias is treated as "no alias"
    var alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim();

    if (alias is not null)
    {
        if (!IsValidAlias(alias))
            return Results.BadRequest(new
            {
                error = "The alias must be 3-32 characters long and contain only letters, digits, '-' and '_'."
            });

        if (reservedAliases.Contains(alias))
            return Results.BadRequest(new { error = $"The alias '{alias}' is reserved." });
    }

    if (request.ExpiresInDays is < MinExpiryDays or > MaxExpiryDays)
        return Results.BadRequest(new
        {
            error = $"'expiresInDays' must be between {MinExpiryDays} and {MaxExpiryDays}."
        });

    using var connection = OpenConnection(connectionString);
    var createdAt = DateTime.UtcNow;
    DateTime? expiresAt = request.ExpiresInDays is int days ? createdAt.AddDays(days) : null;
    string? code = null;

    if (alias is not null)
    {
        // Custom alias: a single attempt, 409 if it is already taken
        if (!TryInsertLink(connection, alias, url, createdAt, expiresAt))
            return Results.Conflict(new { error = $"The alias '{alias}' is already taken." });

        code = alias;
    }
    else
    {
        // Generated code: retry if it is already taken
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var candidate = GenerateCode();
            if (TryInsertLink(connection, candidate, url, createdAt, expiresAt))
            {
                code = candidate;
                break;
            }
        }

        if (code is null)
            return Results.Json(
                new { error = "Could not generate a unique short code. Please try again." },
                statusCode: StatusCodes.Status500InternalServerError);
    }

    var shortUrl = BuildShortUrl(context, code);
    return Results.Ok(new { code, shortUrl, url, createdAt, expiresAt });
});

app.MapGet("/api/links", (int? page, int? pageSize, HttpContext context) =>
{
    var currentPage = page ?? 1;
    var size = pageSize ?? DefaultPageSize;

    if (currentPage < 1)
        return Results.BadRequest(new { error = "'page' must be 1 or greater." });

    if (size < 1 || size > MaxPageSize)
        return Results.BadRequest(new { error = $"'pageSize' must be between 1 and {MaxPageSize}." });

    using var connection = OpenConnection(connectionString);

    // Total number of links, so the client knows how many pages there are
    using var countCommand = connection.CreateCommand();
    countCommand.CommandText = "SELECT COUNT(*) FROM links";
    var total = (long)countCommand.ExecuteScalar()!;

    // One page of links, newest first
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT code, url, clicks, created_at, last_accessed_at, expires_at
        FROM links
        ORDER BY created_at DESC, code
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$limit", size);
    command.Parameters.AddWithValue("$offset", (currentPage - 1) * size);

    var items = new List<object>();
    using var reader = command.ExecuteReader();

    while (reader.Read())
    {
        var code = reader.GetString(0);
        items.Add(new
        {
            code,
            shortUrl = BuildShortUrl(context, code),
            url = reader.GetString(1),
            clicks = reader.GetInt64(2),
            createdAt = reader.GetString(3),
            lastAccessedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
            expiresAt = reader.IsDBNull(5) ? null : reader.GetString(5)
        });
    }

    var totalPages = (int)Math.Ceiling(total / (double)size);

    return Results.Ok(new { page = currentPage, pageSize = size, total, totalPages, items });
});

app.MapGet("/api/links/{code}", (string code) =>
{
    using var connection = OpenConnection(connectionString);

    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT code, url, clicks, created_at, last_accessed_at, expires_at
        FROM links
        WHERE code = $code
        """;
    command.Parameters.AddWithValue("$code", code);

    using var reader = command.ExecuteReader();

    if (!reader.Read())
        return Results.NotFound(new { error = $"No link found with code '{code}'." });

    return Results.Ok(new
    {
        code = reader.GetString(0),
        url = reader.GetString(1),
        clicks = reader.GetInt64(2),
        createdAt = reader.GetString(3),
        lastAccessedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
        expiresAt = reader.IsDBNull(5) ? null : reader.GetString(5)
    });
});

app.MapDelete("/api/links/{code}", (string code) =>
{
    using var connection = OpenConnection(connectionString);

    using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM links WHERE code = $code";
    command.Parameters.AddWithValue("$code", code);

    var deleted = command.ExecuteNonQuery();

    return deleted == 1
        ? Results.NoContent()
        : Results.NotFound(new { error = $"No link found with code '{code}'." });
});

app.MapGet("/{code}", (string code) =>
{
    using var connection = OpenConnection(connectionString);
    var now = DateTime.UtcNow.ToString("O");

    // Count the click and read the URL, but only if the link has not expired
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE links
        SET clicks = clicks + 1,
            last_accessed_at = $now
        WHERE code = $code
          AND (expires_at IS NULL OR expires_at > $now)
        RETURNING url
        """;
    command.Parameters.AddWithValue("$code", code);
    command.Parameters.AddWithValue("$now", now);

    var url = command.ExecuteScalar() as string;

    // 302 (temporary) so browsers don't cache the redirect and skip the counter
    if (url is not null)
        return Results.Redirect(url, permanent: false);

    // Nothing was updated: the link is either expired or does not exist
    if (LinkExists(connection, code))
        return Results.Json(
            new { error = "This link has expired." },
            statusCode: StatusCodes.Status410Gone);

    return Results.NotFound();
});

app.Run();

// Create the links table if it does not exist yet
static void InitializeDatabase(string connectionString)
{
    using var connection = OpenConnection(connectionString);

    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS links (
            code             TEXT PRIMARY KEY,
            url              TEXT NOT NULL,
            created_at       TEXT NOT NULL,
            expires_at       TEXT NULL,
            clicks           INTEGER NOT NULL DEFAULT 0,
            last_accessed_at TEXT NULL
        )
        """;
    command.ExecuteNonQuery();
}

static SqliteConnection OpenConnection(string connectionString)
{
    var connection = new SqliteConnection(connectionString);
    connection.Open();
    return connection;
}

// Insert a link; returns false if the code already exists
static bool TryInsertLink(
    SqliteConnection connection, string code, string url, DateTime createdAt, DateTime? expiresAt)
{
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO links (code, url, created_at, expires_at)
        VALUES ($code, $url, $createdAt, $expiresAt)
        ON CONFLICT(code) DO NOTHING
        """;
    command.Parameters.AddWithValue("$code", code);
    command.Parameters.AddWithValue("$url", url);
    command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
    command.Parameters.AddWithValue("$expiresAt", (object?)expiresAt?.ToString("O") ?? DBNull.Value);

    return command.ExecuteNonQuery() == 1;
}

static bool LinkExists(SqliteConnection connection, string code)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1 FROM links WHERE code = $code";
    command.Parameters.AddWithValue("$code", code);

    return command.ExecuteScalar() is not null;
}

static string BuildShortUrl(HttpContext context, string code) =>
    $"{context.Request.Scheme}://{context.Request.Host}/{code}";

// Accept only absolute http/https URLs
static bool IsValidHttpUrl(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri)
    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

// 3-32 characters: letters, digits, '-' and '_'
static bool IsValidAlias(string alias) =>
    Regex.IsMatch(alias, "^[A-Za-z0-9_-]{3,32}$");

// 7 random Base62 characters
static string GenerateCode()
{
    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    return RandomNumberGenerator.GetString(alphabet, 7);
}

record CreateLinkRequest(string? Url, string? Alias, int? ExpiresInDays);