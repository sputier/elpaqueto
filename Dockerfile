# ElPaqueto container image.
#
#   docker build --build-arg Version=1.2.3 --build-arg REVISION="$(git rev-parse HEAD)" -t elpaqueto .
#
# The base images are pinned to a patch-level tag; Dependabot proposes the updates.
# See docs/user/docker.md for the runtime contract (user, volume, port, health check, shutdown).

# Version of the application, also published as the image's version label.
ARG Version=0.1.0-prerelease
# Commit the image is built from (CI passes the full SHA). Empty for local builds.
ARG REVISION=
# Source repository published as a label. Forks may override it.
ARG SOURCE_URL=https://github.com/sputier/ElPaqueto

## Build stage: runs on the build machine's platform and cross-compiles for the target architecture,
## so that a multi-platform build does not run the SDK under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.404-alpine3.24 AS build
ARG TARGETARCH
WORKDIR /repo

# Restore in its own layer, so that it is cached until a project file or a package version changes.
# The build props import eng/ElPaqueto.Build.props, so the repository layout is kept: /repo/src/<project>.
COPY global.json nuget.config Directory.Packages.props ./
COPY eng/ElPaqueto.Build.props eng/
COPY src/Directory.Build.props src/Directory.Build.targets src/
COPY src/*/*.csproj src/
# COPY flattens the project files: move each one back into the folder named after it.
RUN for file in src/*.csproj; do \
        name="$(basename "$file" .csproj)"; \
        mkdir -p "src/$name" && mv "$file" "src/$name/"; \
    done
RUN dotnet restore src/ElPaqueto/ElPaqueto.csproj --arch "$TARGETARCH"

## Publish stage: builds the application for the target architecture.
FROM build AS publish
ARG TARGETARCH
ARG Version
ARG REVISION
COPY src/ src/
# CommitHash and CommitBranch are set so that the build does not call git: the context has no .git folder.
RUN dotnet publish src/ElPaqueto/ElPaqueto.csproj \
        --configuration Release \
        --output /app \
        --no-restore \
        --arch "$TARGETARCH" \
        -p Version="$Version" \
        -p CommitHash="${REVISION:-unknown}" \
        -p CommitBranch=unknown \
        -p DebugType=none \
        -p DebugSymbols=false \
        -p GenerateDocumentationFile=false \
        -p UseAppHost=false

## Runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-alpine3.24 AS runtime
ARG Version
ARG REVISION
ARG SOURCE_URL

LABEL org.opencontainers.image.title="ElPaqueto" \
      org.opencontainers.image.description="Self-hosted private NuGet server" \
      org.opencontainers.image.source="$SOURCE_URL" \
      org.opencontainers.image.url="$SOURCE_URL" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="$Version" \
      org.opencontainers.image.revision="$REVISION"

# Security fixes published by Alpine since the base image was built, then cultures and time zones
# (the base image runs in invariant globalization mode).
# /data belongs to the non-root "app" user of the base image ($APP_UID, 1654). A new named volume copies
# this ownership; a bind mount keeps the host's, so the host folder must belong to that UID.
# The database folder is owner-only: it holds the security database.
RUN apk upgrade --no-cache \
    && apk add --no-cache icu-libs icu-data-full tzdata \
    && mkdir -p /data/packages /data/symbols /data/db \
    && chown -R "$APP_UID:$APP_UID" /data \
    && chmod 0750 /data /data/packages /data/symbols \
    && chmod 0700 /data/db

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_HTTP_PORTS=8080 \
    # Graceful shutdown budget, kept below the 10 s that `docker stop` waits before it kills the process.
    # Raise both together for long uploads (`docker stop -t`, Compose `stop_grace_period`).
    DOTNET_SHUTDOWNTIMEOUTSECONDS=8 \
    # Packages, symbols and both SQLite databases live on the /data volume.
    Storage__Type=FileSystem \
    Storage__Path=/data \
    Search__Type=Database \
    Database__Type=Sqlite \
    Database__ConnectionString="Data Source=/data/db/elpaqueto.db" \
    Security__Database__ConnectionString="Data Source=/data/db/auth.db"

# The application files stay owned by root: the process cannot modify them.
WORKDIR /app
COPY --from=publish /app .

USER $APP_UID:$APP_UID
VOLUME ["/data"]
EXPOSE 8080

# Anonymous liveness endpoint (HealthCheck:Path, /health by default). The request comes from loopback,
# so it is allowed over plain HTTP. busybox wget is part of the base image; curl is not installed.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:8080/health || exit 1

# Exec form: dotnet is PID 1 and receives the stop signal itself.
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "ElPaqueto.dll"]
