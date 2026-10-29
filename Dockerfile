# syntax=docker/dockerfile:1

# In a git worktree .git is only a pointer into the main repository, which lies outside the build context. Pass that
# repository as the "git" context (scripts/docker-build.sh does): docker build --build-context git=<git common dir> .
# Other checkouts need nothing: this empty stage stands in for it.
FROM scratch AS git

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY GenOnlineService/GenOnlineService.csproj GenOnlineService/
RUN dotnet restore GenOnlineService/GenOnlineService.csproj

COPY GenOnlineService/ GenOnlineService/
# The version (git tag based, see GoResolveVersion in the csproj) is computed by the build with git describe and
# reported by /healthz, so .git is copied after restore. Without tags the build reports 0.0.0+<commit>.
COPY .git .git
RUN --mount=type=bind,from=git,target=/git \
    git -C / config --global --add safe.directory '*' \
    && if [ -f .git ]; then \
        worktree="$(sed -n 's#^gitdir: .*/\.git/worktrees/##p' .git)"; \
        if [ -z "$worktree" ] || [ ! -d "/git/worktrees/$worktree" ]; then \
            echo 'This is a git worktree: build with scripts/docker-build.sh or pass --build-context git=<git common dir>' >&2; \
            exit 1; \
        fi; \
        export GIT_DIR="/git/worktrees/$worktree" GIT_WORK_TREE=/src; \
    fi \
    && dotnet publish GenOnlineService/GenOnlineService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish/ ./

# The host mounts its own appsettings.json (secrets are required, so the image cannot run without one). The shipped
# sample binds localhost with HTTPS, which has no certificate here, and a Kestrel:Endpoints section would override
# ASPNETCORE_HTTP_PORTS below.
RUN rm -f /app/appsettings.json

# Crash dumps are written to Exceptions/ (Program.cs); the root filesystem may be read-only, so the directory
# must exist and belong to the non-root user. Hosts mount data/, crcfiles/, Exceptions/ and appsettings.json.
RUN mkdir -p /app/Exceptions /app/crcfiles \
    && chown "$APP_UID:$APP_UID" /app/Exceptions

USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "GenOnlineService.dll"]
