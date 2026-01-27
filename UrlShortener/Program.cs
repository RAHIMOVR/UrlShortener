using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory storage: short code -> original URL
var links = new Dictionary<string, string>();

app.MapPost("/api/links", (CreateLinkRequest request, HttpContext context) =>
{
    var code = GenerateCode();
    links[code] = request.Url;

    var shortUrl = $"{context.Request.Scheme}://{context.Request.Host}/{code}";
    return Results.Ok(new { code, shortUrl, url = request.Url });
});

app.MapGet("/{code}", (string code) =>
    links.TryGetValue(code, out var url)
        ? Results.Redirect(url)
        : Results.NotFound());

app.Run();

// 7 random Base62 characters
static string GenerateCode()
{
    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    return RandomNumberGenerator.GetString(alphabet, 7);
}

record CreateLinkRequest(string Url);