# UrlShortener

A minimal URL shortener built with ASP.NET Core Minimal API (.NET 8), SQLite and a plain HTML/JavaScript frontend.

## Features

- Shorten any absolute `http` or `https` URL
- Optional custom aliases (3-32 characters: letters, digits, `-` and `_`)
- Optional expiration (1-365 days); expired links return `410 Gone`
- Click tracking with last access time
- List links with pagination, newest first
- Delete links
- Consistent JSON error responses
- Simple web interface with no frameworks or build step

## Tech stack

- .NET 8, ASP.NET Core Minimal API
- SQLite via `Microsoft.Data.Sqlite` (raw, parameterized SQL)
- Plain HTML, CSS and JavaScript served from `wwwroot`

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Getting started

```bash
git clone https://github.com/<your-username>/UrlShortener.git
cd UrlShortener
dotnet run
```

The app prints its address on startup, for example `http://localhost:5019`. Open it in a browser to use the web interface.

The SQLite database `urls.db` is created automatically in the project folder on first run.

## API

All error responses use the same format:

```json
{ "error": "Description of the problem." }
```

| Method | Route | Description |
| --- | --- | --- |
| POST | `/api/links` | Create a short link |
| GET | `/{code}` | Redirect to the original URL |
| GET | `/api/links/{code}` | Get stats for one link |
| GET | `/api/links?page=1&pageSize=20` | List links, newest first |
| DELETE | `/api/links/{code}` | Delete a link |

### Create a short link

`POST /api/links`

```json
{
  "url": "https://example.com/some/long/path",
  "alias": "my-link",
  "expiresInDays": 30
}
```

`alias` and `expiresInDays` are optional.

Response `200 OK`:

```json
{
  "code": "my-link",
  "shortUrl": "http://localhost:5019/my-link",
  "url": "https://example.com/some/long/path",
  "createdAt": "2026-11-21T15:50:21.0502355Z",
  "expiresAt": "2026-12-21T15:50:21.0502355Z"
}
```

Errors: `400` for invalid input, `409` if the alias is already taken.

### Redirect

`GET /{code}`

Redirects with `302 Found` and counts the click. A `302` is used instead of a `301` so that browsers do not cache the redirect and skip the counter.

Errors: `404` if the code does not exist, `410` if the link has expired.

### Get stats for one link

`GET /api/links/{code}`

Response `200 OK`:

```json
{
  "code": "my-link",
  "url": "https://example.com/some/long/path",
  "clicks": 3,
  "createdAt": "2026-11-21T15:50:21.0502355Z",
  "lastAccessedAt": "2026-11-21T16:02:10.4417203Z",
  "expiresAt": "2026-12-21T15:50:21.0502355Z"
}
```

Errors: `404` if the code does not exist.

### List links

`GET /api/links?page=1&pageSize=20`

`page` defaults to 1. `pageSize` defaults to 20, maximum 100.

Response `200 OK`:

```json
{
  "page": 1,
  "pageSize": 20,
  "total": 1,
  "totalPages": 1,
  "items": [
    {
      "code": "my-link",
      "shortUrl": "http://localhost:5019/my-link",
      "url": "https://example.com/some/long/path",
      "clicks": 3,
      "createdAt": "2026-11-21T15:50:21.0502355Z",
      "lastAccessedAt": "2026-11-21T16:02:10.4417203Z",
      "expiresAt": "2026-12-21T15:50:21.0502355Z"
    }
  ]
}
```

### Delete a link

`DELETE /api/links/{code}`

Response `204 No Content`.

Errors: `404` if the code does not exist.

## Project structure

```
UrlShortener/
  Program.cs            API, database access and validation
  wwwroot/index.html    Web interface
  UrlShortener.csproj
  README.md
```
