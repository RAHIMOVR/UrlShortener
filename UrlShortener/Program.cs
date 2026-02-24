using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Don't throw on bad input (broken JSON, wrong types); return an empty 400
// instead, which the status code handler below turns into a JSON error
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var app = builder.Build();

// Database: urls.db in the project folder
var dbPath = Path.Combine(app.Environment.ContentRootPath, "urls.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

InitializeDatabase(connectionString);

// Unhandled exceptions become a JSON 500 (or the exception's own status code)
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

    var (status, message) = exception is BadHttpRequestException badRequest
        ? (badRequest.StatusCode, "The request is invalid.")
        : (StatusCodes.Status500InternalServerError, "An unexpected error occurred.");

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
}));

// Error responses without a body (unknown routes, bad input, wrong method) get a JSON body
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;

    var message = response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid. Check the JSON body and query parameters.",
        StatusCodes.Status404NotFound => "Not found.",
        StatusCodes.Status405MethodNotAllowed => "This HTTP method is not allowed for this route.",
        StatusCodes.Status415UnsupportedMediaType => "The request body must be JSON (Content-Type: application/json).",
        _ => "The request failed."
    };

    await response.WriteAsJsonAsync(new ErrorResponse(message));
});

// Serve wwwroot/index.html at "/". These run before routing so that
// files like /index.html are not caught by the /{code} redirect route.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

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
        return Error(StatusCodes.Status400BadRequest, "The 'url' field is required.");

    var url = request.Url.Trim();

    if (!IsValidHttpUrl(url))
        return Error(StatusCodes.Status400BadRequest, "The URL must be an absolute http or https URL.");

    // An empty alias is treated as "no alias"
    var alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim();

    if (alias is not null)
    {
        if (!IsValidAlias(alias))
            return Error(StatusCodes.Status400BadRequest,
                "The alias must be 3-32 characters long and contain only letters, digits, '-' and '_'.");

        if (reservedAliases.Contains(alias))
            return Error(StatusCodes.Status400BadRequest, $"The alias '{alias}' is reserved.");
    }

    if (request.ExpiresInDays is < MinExpiryDays or > MaxExpiryDays)
        return Error(StatusCodes.Status400BadRequest,
            $"'expiresInDays' must be between {MinExpiryDays} and {MaxExpiryDays}.");

    using var connection = OpenConnection(connectionString);
    var createdAt = DateTime.UtcNow;
    DateTime? expiresAt = request.ExpiresInDays is int days ? createdAt.AddDays(days) : null;
    string? code = null;

    if (alias is not null)
    {
        // Custom alias: a single attempt, 409 if it is already taken
        if (!TryInsertLink(connection, alias, url, createdAt, expiresAt))
            return Error(StatusCodes.Status409Conflict, $"The alias '{alias}' is already taken.");

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
            return Error(StatusCodes.Status500InternalServerError,
                "Could not generate a unique short code. Please try again.");
    }

    var shortUrl = BuildShortUrl(context, code);
    return Results.Ok(new { code, shortUrl, url, createdAt, expiresAt });
});

app.MapGet("/api/links", (int? page, int? pageSize, HttpContext context) =>
{
    var currentPage = page ?? 1;
    var size = pageSize ?? DefaultPageSize;

    if (currentPage < 1)
        return Error(StatusCodes.Status400BadRequest, "'page' must be 1 or greater.");

    if (size < 1 || size > MaxPageSize)
        return Error(StatusCodes.Status400BadRequest, $"'pageSize' must be between 1 and {MaxPageSize}.");

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
        return Error(StatusCodes.Status404NotFound, $"No link found with code '{code}'.");

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
        : Error(StatusCodes.Status404NotFound, $"No link found with code '{code}'.");
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
        return Error(StatusCodes.Status410Gone, "This link has expired.");

    return Error(StatusCodes.Status404NotFound, $"No link found with code '{code}'.");
});

app.Run();

// Every error response uses the same { "error": "..." } shape
static IResult Error(int statusCode, string message) =>
    Results.Json(new ErrorResponse(message), statusCode: statusCode);

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

record ErrorResponse(string Error);