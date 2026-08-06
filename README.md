# FlexFetch

FlexFetch: The Flexible, Plugin-based Media Downloader.

A self-hosted web download service: paste a resource link and the server handles the whole flow of "identify -> parse -> download -> save", with real-time task progress management on a web page.

## Features

- Download plain file links, social media videos (Twitter/X), video sites (YouTube) and playlists
- Share completed downloads via unguessable random links (read-only for visitors)
- Simple account system (user / admin) with clear permission levels
- Centralized login-state (Cookie pool) maintained by admins for all downloaders
- One codebase deployed on Windows / Linux / Docker

## Requirements

- .NET 10 SDK

## Build and test

```bash
dotnet build FlexFetch.slnx
dotnet test FlexFetch.slnx
```

## Project structure

```
FlexFetch.sln
FlexFetch/                 # ASP.NET Core Web (net10.0)
  Program.cs / appsettings.json
  Domain/  Data/  Config/  Services/  Api/  HostedServices/  wwwroot/
FlexFetch.Tests/           # MSTest
deploy/                    # deploy.ps1, Dockerfile, compose.yaml
```
