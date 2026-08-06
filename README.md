# FlexFetch

FlexFetch: The Flexible, Plugin-based Media Downloader.

A self-hosted web download service: paste a resource link and the server handles the whole flow of "identify -> parse -> download -> save", with real-time task progress management on a web page.

## Features

- Download plain file links, social media videos (Twitter/X), video sites (YouTube) and playlists
- Browser-assisted parsing of arbitrary web pages for embedded video resources
- Share completed downloads via unguessable random links (read-only for visitors)
- Simple account system (user / admin) with clear permission levels
- Centralized login-state (Cookie pool) maintained by admins for all downloaders
- Headless browser (system Chrome/Edge) with anti-detection stealth and profile persistence
- Global proxy with CIDR bypass and composable route policies
- One codebase deployed on Windows / Linux / Docker

## Requirements

- .NET 10 SDK
- External components (auto-installed or provided): yt-dlp, ffmpeg, deno, a system browser (Chrome/Edge)

## Build and test

```bash
dotnet build FlexFetch.slnx
dotnet test FlexFetch.slnx
```

## Run

```bash
dotnet run --project FlexFetch
# Web UI: http://localhost:5000  (or https://localhost:5001)
```

On first start, set `Admin:InitialPassword` (config or environment) to bootstrap the admin account:

```bash
Admin:InitialPassword=change-me dotnet run --project FlexFetch
```

## Deploy

A unified deployment script covers Linux / Windows / Docker:

```bash
pwsh ./deploy/deploy.ps1 -AppDir ./flexfetch-app -DataDir ./flexfetch-data -Port 8080
# Options: -Proxy <url> (install dependencies and route downloads through a proxy)
#          -Mode Auto|Docker|Systemd|WindowsService|None
#          -SkipInstall
```

The script is idempotent: re-running it does not reinstall or break existing data. The data directory is independent of the program directory, so upgrades never lose data. See `deploy/compose.yaml` for the Docker Compose template.

## Configuration

Runtime configuration lives in the data directory (LiteDB) and is managed by admins via the web UI (`/cookie.html`, `/system.html`) or the REST API (`GET/PUT /api/config`). Defaults are declared in `FlexFetch/Config/ConfigRegistry.cs`.

Key settings: registration policy, session hours, inactive cleanup days, max concurrency, retries, timeouts, proxy, CIDR bypass file, cookie refresh period, component auto-upgrade, data directory, share token lifetime.

## Project structure

```
FlexFetch.slnx
FlexFetch/                 # ASP.NET Core Web (net10.0)
  Program.cs / appsettings.json
  Domain/  Data/  Config/  Services/  Api/  HostedServices/  wwwroot/
FlexFetch.Tests/           # MSTest
deploy/                    # deploy.ps1, Dockerfile, compose.yaml
```

## API overview

| Method | Path | Access |
|---|---|---|
| POST | /api/auth/register · /login · /logout | public |
| GET/POST | /api/users/pending · /{id}/approve · /{id}/disable | admin |
| GET/POST | /api/tasks · /{id} · /{id}/retry · /{id}/share | owner |
| GET | /api/share/{token} · /api/share/{token}/file | public (read-only) |
| GET/POST | /api/cookies · /import · /groups | admin |
| GET | /api/system/info | signed-in |
| POST | /api/system/shutdown · /upgrade | admin |
| GET/PUT | /api/config | admin |
