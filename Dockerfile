# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DevCommander.sln ./
COPY src/DevCommander/DevCommander.csproj src/DevCommander/
COPY tst/DevCommander.Tests/DevCommander.Tests.csproj tst/DevCommander.Tests/
RUN dotnet restore DevCommander.sln
COPY . .
RUN dotnet publish src/DevCommander/DevCommander.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        bubblewrap \
        ca-certificates \
        curl \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

# Pin CLI installs at image build time. Versions are intentional pins for reproducibility.
# Operators must enable unprivileged user namespaces on the host (e.g. sysctl
# kernel.unprivileged_userns_clone=1) or bubblewrap probes will fail closed.
RUN npm install -g @anthropic-ai/claude-code@1.0.0 2>/dev/null || true \
    && npm install -g @openai/codex@0.1.0 2>/dev/null || true \
    && npm install -g opencode-ai@0.1.0 2>/dev/null || true \
    && echo "Cursor agent CLI must be mounted or installed by the operator (executable: agent)."

# Non-root runtime user
RUN useradd -m -u 10001 -s /bin/bash devcommander \
    && mkdir -p /data \
    && chown -R devcommander:devcommander /data

WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    DevCommander__DataRoot=/data \
    DOTNET_EnableDiagnostics=0

USER devcommander
VOLUME ["/data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/health || exit 1

# Sandbox / CLI probe hints (runtime capability probe runs at process start).
CMD ["dotnet", "DevCommander.dll"]
