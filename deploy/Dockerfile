# FlexFetch - multi-stage image built from source.
# Nothing the app can install itself is baked in: yt-dlp, deno, ffmpeg, the
# Playwright Firefox AND its OS-level libraries are fetched at runtime into
# the /data volume (ops.autoInstallDeps) - see BrowserInstaller. Only curl is
# provided for in-container debugging.

# Stage 1: build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FlexFetch/FlexFetch.csproj FlexFetch/
COPY FlexFetch.Tests/FlexFetch.Tests.csproj FlexFetch.Tests/
RUN dotnet restore FlexFetch/FlexFetch.csproj
COPY FlexFetch/ FlexFetch/
COPY FlexFetch.Tests/ FlexFetch.Tests/
RUN dotnet publish FlexFetch/FlexFetch.csproj -c Release -o /app/publish

# Stage 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
# Template for the editable /data configuration: Program.cs seeds
# /data/appsettings.json from this file on first start. The name must NOT
# match appsettings.{Environment}.json - the host would auto-load it and
# its Kestrel section would override the /data file's endpoint removals.
COPY deploy/appsettings.docker.json appsettings.template.json

# Official Ubuntu sources are used as-is. Networks that cannot reach them
# can point apt at a mirror for the build without changing the image:
#   docker build --build-arg apt_mirror=<host/ubuntu> ...
ARG apt_mirror=""
RUN if [ -n "${apt_mirror}" ]; then \
        sed -i -e "s|http://archive.ubuntu.com/ubuntu|https://${apt_mirror}|g" \
            -e "s|http://security.ubuntu.com/ubuntu|https://${apt_mirror}|g" \
            /etc/apt/sources.list.d/ubuntu.sources; \
    fi \
    && apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# /data is the app's self-discovered data directory (database, logs,
# components, the editable appsettings.json): a volume is expected there.
# Ports are NOT pinned here: the endpoints live in /data/appsettings.json,
# and the base image's HTTP_PORTS default only applies when no endpoint
# configuration exists. Certificates are read from the /certs mount; when
# they cannot be used the app starts without https (http keeps serving).
# Runtime apt (browser OS dependencies) uses the official archive over
# https; set FLEXFETCH_APT_MIRROR=<host/path> (e.g. mirrors.example.com/ubuntu)
# to point that one-time install at a mirror instead.
RUN mkdir -p /data && chmod 755 /data

EXPOSE 5080 5081
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "FlexFetch.dll"]
