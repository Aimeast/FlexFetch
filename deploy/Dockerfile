# FlexFetch - multi-stage image built from source.
# External components (yt-dlp) are installed by the app's dependency
# self-check at runtime or can be pre-installed by extending this image.

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

# Runtime tooling: curl (dependency installs), ffmpeg (audio/video merge).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Data directory must be writable and is expected to be mounted as a volume.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV storage:dataDir=/data

RUN mkdir -p /data && chmod 755 /data

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "FlexFetch.dll"]
