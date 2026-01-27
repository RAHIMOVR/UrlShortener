using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory storage: short code -> original URL
var links = new Dictionary<string, string>();

const int MaxCodeAttempts = 10;

app.MapPost("/api/links", (CreateLinkRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "The 'url' field is required." });

    var url = request.Url.Trim();

    if (!IsValidHttpUrl(url))
        return Results.BadRequest(new { error = "The URL must be an absolute http or https URL." });

    // Generate a code, retrying if it is already taken
    string? code = null;
    for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
    {
        var candidate = GenerateCode();
        if (!links.ContainsKey(candidate))
        {
            code = candidate;
            break;
        }
    }

    if (code is null)
        return Results.Json(
            new { error = "Could not generate a unique short code. Please try again." },
            statusCode: StatusCodes.Status500InternalServerError);

    links[code] = url;

    var shortUrl = $"{context.Request.Scheme}://{context.Request.Host}/{code}";
    return Results.Ok(new { code, shortUrl, url });
});

app.MapGet("/{code}", (string code) =>
    links.TryGetValue(code, out var url)
        ? Results.Redirect(url)
        : Results.NotFound());

app.Run();

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