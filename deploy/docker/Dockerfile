# syntax=docker/dockerfile:1
FROM node:24-bookworm-slim AS web
WORKDIR /web
RUN npm install --global pnpm@11.25.0
COPY src/KommitEye.Web/package.json src/KommitEye.Web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/KommitEye.Web/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /source
COPY Directory.Build.props ./
COPY src/ ./src/
COPY KommitEye/*.example.conf KommitEye/system-prompt*.example.txt ./KommitEye/
RUN dotnet publish src/KommitEye.Server/KommitEye.Server.csproj -c Release --no-self-contained -p:UseAppHost=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl openssl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 kommiteye && useradd --uid 10001 --gid 10001 --no-create-home --home-dir /data/home kommiteye
WORKDIR /app
COPY --from=server /out/ ./
COPY --from=web /web/dist/ ./wwwroot/
COPY --chmod=755 deploy/docker/entrypoint.sh deploy/docker/init.sh /opt/kommiteye/
ENV HOME=/data/home DOTNET_EnableDiagnostics=0
USER 10001:10001
EXPOSE 5080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=4 CMD curl --fail --silent http://127.0.0.1:5080/api/v1/setup > /dev/null || exit 1
ENTRYPOINT ["/opt/kommiteye/entrypoint.sh"]
CMD ["--config", "/config/server.json"]
