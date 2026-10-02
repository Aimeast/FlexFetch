# FlexFetch - runtime image assembled from a PRE-PUBLISHED folder.
# Two steps; dotnet publish must run on a machine holding the working tree
# (it captures the git reference for System information - a container build
# never sees .git, so publishing inside the image would leave the Git log
# as "unknown"):
#   dotnet publish FlexFetch/FlexFetch.csproj -c Release -o publish
#   docker build -f deploy/Dockerfile -t flexfetch:latest .
# deploy.ps1 (Docker mode) runs both steps automatically.
#
# Nothing the app can install itself is baked in: yt-dlp, deno, ffmpeg, the
# Playwright Firefox AND its OS-level libraries are fetched at runtime into
# the /data volume (ops.autoInstallDeps) - see BrowserInstaller. Only curl
# is provided for in-container debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY publish/ .
COPY deploy/appsettings.docker.json appsettings.template.json

# Official Ubuntu sources are used as-is. Networks that cannot reach them
# can point the build's apt at a mirror through the http_proxy/https_proxy
# build-args; runtime browser-dependency installs support the
# FLEXFETCH_APT_MIRROR environment variable instead (see BrowserInstaller).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# /data is the app's self-discovered data directory (database, logs,
# components, the editable appsettings.json): a volume is expected there.
# Ports are NOT pinned here: the endpoints live in /data/appsettings.json,
# and the base image's HTTP_PORTS default only applies when no endpoint
# configuration exists. Certificates are read from the /certs mount; when
# they cannot be used the app starts without https (http keeps serving).
RUN mkdir -p /data && chmod 755 /data

EXPOSE 5080 5081
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "FlexFetch.dll"]
