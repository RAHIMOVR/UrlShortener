using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Database: urls.db in the project folder
var dbPath = Path.Combine(app.Environment.ContentRootPath, "urls.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

InitializeDatabase(connectionString);

const int MaxCodeAttempts = 10;

app.MapPost("/api/links", (CreateLinkRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "The 'url' field is required." });

    var url = request.Url.Trim();

    if (!IsValidHttpUrl(url))
        return Results.BadRequest(new { error = "The URL must be an absolute http or https URL." });

    using var connection = OpenConnection(connectionString);
    var createdAt = DateTime.UtcNow;

    // Generate a code, retrying if it is already taken
    string? code = null;
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

    var shortUrl = $"{context.Request.Scheme}://{context.Request.Host}/{code}";
    return Results.Ok(new { code, shortUrl, url, createdAt });
});

app.MapGet("/{code}", (string code) =>
{
    using var connection = OpenConnection(connectionString);

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT url FROM links WHERE code = $code";
    command.Parameters.AddWithValue("$code", code);

    var url = command.ExecuteScalar() as string;

    return url is null ? Results.NotFound() : Results.Redirect(url);
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

// 7 random Base62 characters
static string GenerateCode()
{
    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    return RandomNumberGenerator.GetString(alphabet, 7);
}

record CreateLinkRequest(string? Url);