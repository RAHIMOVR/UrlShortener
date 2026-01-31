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

    using var connection = OpenConnection(connectionString);
    var createdAt = DateTime.UtcNow;
    string? code = null;

    if (alias is not null)
    {
        // Custom alias: a single attempt, 409 if it is already taken
        if (!TryInsertLink(connection, alias, url, createdAt))
            return Results.Conflict(new { error = $"The alias '{alias}' is already taken." });

        code = alias;
    }
    else
    {
        // Generated code: retry if it is already taken
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var candidate = GenerateCode();
            if (TryInsertLink(connection, candidate, url, createdAt))
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

    var shortUrl = $"{context.Request.Scheme}://{context.Request.Host}/{code}";
    return Results.Ok(new { code, shortUrl, url, createdAt });
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

app.MapGet("/{code}", (string code) =>
{
    using var connection = OpenConnection(connectionString);

    // Count the click and read the URL in a single statement
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE links
        SET clicks = clicks + 1,
            last_accessed_at = $now
        WHERE code = $code
        RETURNING url
        """;
    command.Parameters.AddWithValue("$code", code);
    command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

    var url = command.ExecuteScalar() as string;

    // 302 (temporary) so browsers don't cache the redirect and skip the counter
    return url is null ? Results.NotFound() : Results.Redirect(url, permanent: false);
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
static bool TryInsertLink(SqliteConnection connection, string code, string url, DateTime createdAt)
{
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO links (code, url, created_at)
        VALUES ($code, $url, $createdAt)
        ON CONFLICT(code) DO NOTHING
        """;
    command.Parameters.AddWithValue("$code", code);
    command.Parameters.AddWithValue("$url", url);
    command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));

    return command.ExecuteNonQuery() == 1;
}

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

record CreateLinkRequest(string? Url, string? Alias);