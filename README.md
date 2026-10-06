# FlexFetch

FlexFetch: The Flexible, Plugin-based Media Downloader.

A self-hosted web download service: paste a resource link and the server handles the whole flow of "identify -> parse -> download -> save", with real-time task progress management on a web page.

## Features

- Download plain file links, social media videos, video sites and playlists
- HLS direct links (.m3u/.m3u8) are downloaded as a single video file via yt-dlp + ffmpeg; plain m3u playlists are expanded into one child task per entry
- Generic yt-dlp fallback downloader for any other video site yt-dlp supports
- Browser-assisted media detection: a headless Firefox sniffs network responses for media streams on arbitrary web pages
- Direct media links (by extension, or by served Content-Type) are downloaded straight away by the generic file downloader
- Site login-state as an admin-imported session snapshot (cookie jar + visitor identity), kept healthy by a scheduled probe / canary re-export, with a self-managed PO token provider (deno-based) for flagged-IP postures
- Share completed downloads via unguessable random links (read-only for visitors)
- Simple account system (user / admin) with clear permission levels, self-service password change, and per-browser anonymous guest sessions when anonymous access is on
- Global proxy with composable route rules: domain-suffix and CIDR matchers, a per-rule action and a default action
- Self-installing external components: yt-dlp, ffmpeg, deno, Node.js and a Playwright-managed Firefox are installed at startup when missing and auto-upgraded on schedule
- One codebase deployed on Windows / Linux / Docker

## Downloader selection

Downloader plugins are self-discovered via reflection - adding a new `IDownloader` requires no manual registration. A site-specific downloader that claims a URL is used alone: it fails fast with no fallback. Only when no specific downloader matches is the generic chain tried in its fixed order (yt-dlp -> HTML -> Browser -> generic file), with the generic file downloader as the guaranteed last resort. Direct media links - identified by URL extension, or by HEAD-probed Content-Type when the URL carries no extension - are promoted to the front of the chain so they download straight away, skipping the slow yt-dlp attempt. Playlist manifests (.m3u/.m3u8/.mpd, by extension or by mpegurl/dash Content-Type) are the exception: they are parsed by yt-dlp first, so an HLS manifest downloads as one merged video file and a plain m3u list expands into child tasks.

## Requirements

- .NET 10 SDK
- Everything external (yt-dlp, ffmpeg, deno, Node.js, Playwright Firefox) is self-installed at startup when missing; a pre-provided installation is reused

## Build and test

```bash
dotnet build FlexFetch.slnx
dotnet test FlexFetch.slnx
```

## Run

```bash
dotnet run --project FlexFetch
# Web UI: http://localhost:5138 (Development launch profile)
```

On first start, when the database has no users, the initial admin account `admin` is created. The password comes from `Admin:InitialPassword` (config or environment); in Development it defaults to `admin`; otherwise a random 5-character password is generated and printed to the console only - never to the log file:

```bash
Admin:InitialPassword=change-me dotnet run --project FlexFetch
```

A lost password can be reset from the server shell (SSH / host console - this is not a web UI feature). Stop the running app first - the database is locked while it runs - then run the command in the form `--reset-password <userName> <newPassword>`:

```bash
# from source (everything after -- is passed to FlexFetch)
dotnet run --project FlexFetch -- --reset-password admin new-password

# installed binary, run in the app directory
FlexFetch.exe --reset-password admin new-password            # Windows
dotnet FlexFetch.dll --reset-password admin new-password     # Linux

# Docker: stop the container, run a one-off container on the same data volume
docker run --rm -v /data:/data flexfetch:latest --reset-password admin new-password
```

The command swaps the stored hash in place - the user id is kept, so task ownership and share links survive - requires a new password of at least 5 characters, and exits without starting the web app. Start the app again afterwards.

## Usage

1. **Wait for the first-start component install.** On a fresh deployment the app installs missing components (yt-dlp, ffmpeg, deno, Node.js, Playwright Firefox) right after startup. Watch the log: `Installing missing components: ...` followed by `All components ready: ...`. On a fresh machine this downloads a few hundred megabytes and can take several minutes (installs go through the configured proxy) - do not submit downloads before it finishes. The System page lists the detected component versions once ready. Disabling `ops.autoInstallDeps` skips the install entirely (the log says what is missing).
2. **Sign in.** Open the web UI and sign in as `admin` with the initial password (printed to the console on a production install, `admin` in Development), then change it on the Account page. Others can register from the sign-in view and are activated by an admin; with anonymous access on (the default), a browser without an account can also submit downloads as a guest - its tasks stay private to that browser.
3. **Submit a download.** Paste a link into the form on the Tasks page and submit. The task list shows live progress; the finished file downloads from the task row, and a failed task can be retried from there.
4. **Optional setup (admin).** On the Session page, paste cookie text to import a login session for sites that need one (signed-in content, flagged-IP postures). From a finished task's row on the Tasks page, create an unguessable read-only share link for visitors. Proxy and route rules are edited in `appsettings.json` inside the data directory (see Configuration).

## Deploy

A unified deployment script covers Linux / Windows / Docker:

```bash
pwsh ./deploy/deploy.ps1 -AppDir ./flexfetch-app -DataDir ./flexfetch-data -Port 8080
# Options: -Proxy <url> (install dependencies and route downloads through a proxy)
#          -Mode Auto|Docker|Systemd|WindowsService|None
#          -SkipInstall
```

The script is idempotent: re-running it does not reinstall or break existing data. The data directory is independent of the program directory, so upgrades never lose data. See `deploy/compose.yaml` for the Docker Compose template: the container needs no environment variables - `/data/appsettings.json` (seeded on first start) owns the endpoints (http 5080 redirecting to https 5081), the proxy policy and every other tunable, and survives container recreation. The image is assembled from pre-published output; building from source inside the container is not supported.

## Configuration

Runtime configuration lives in the data directory as an editable `appsettings.json`: seeded from the bundled template on first start, never overwritten again, and loaded with reloadOnChange (consumers re-read it live where supported, e.g. the network proxy policy). Defaults are declared in `FlexFetch/Config/ConfigRegistry.cs`. There is no config REST API - admins edit the file directly (in a deployment it sits in the data volume).

Key settings: registration policy, session hours, inactive cleanup days, anonymous access, max concurrency, retries, timeouts, proxy, route rules (domain suffixes / CIDR files) with default action, session maintenance period, component auto-upgrade, data directory, share token lifetime.

## Project structure

```
FlexFetch.slnx
FlexFetch/                 # ASP.NET Core Web (net10.0)
  Program.cs               # browser-install child entry + host wiring
  Startup/                 # runtime setup, logging, pipeline, service registration
  Entities/  Enums/        # domain models and enums
  Data/  Config/           # LiteDB repositories, config registry, user config file
  Services/                # domain services (root) + grouped folders
    Downloaders/           # IDownloader plugins, DownloaderFactory, YtdlpService
    Session/               # Playwright Firefox, session snapshot / probe / export, PO token provider
    Routing/               # proxy routing: route rules, domain-suffix and CIDR matchers
    Tasks/                 # TaskService, state machine, ITaskExecutor
  Api/  HostedServices/  wwwroot/
FlexFetch.Tests/           # MSTest
deploy/                    # deploy.ps1, Dockerfile, compose.yaml, appsettings.docker.json
```

## API overview

| Method | Path | Access |
|---|---|---|
| POST · GET | /api/auth/register · /login · /logout · /status | public |
| POST · GET | /api/auth/change-password · /me | signed-in |
| GET/POST/DELETE | /api/users/pending · /{id}/approve · /{id}/disable · /{id} | admin |
| GET/POST/DELETE | /api/tasks · /{id}/retry · /{id}/share · /{id}/file · /{id} | signed-in or guest |
| GET | /api/share/{token} · /api/share/{token}/file | public (read-only) |
| POST/GET | /api/session/import · /export · /export/status · /canary · /pot-status · /diagnose | admin |
| GET | /api/system/info | signed-in |
| POST/GET | /api/system/shutdown · /upgrade · /upgrade/status | admin |
