# syntax=docker/dockerfile:1
# Server container for browser-hosted Lorekeeper. The build stage compiles the
# .NET application and the contained Lorekeeper Press renderer exactly as the
# repository build does: the csproj invokes eng/BuildPressRuntime.ps1, so the
# stage provides pwsh and the Cargo toolchain pinned by rust-toolchain.toml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential curl \
    && rm -rf /var/lib/apt/lists/*
RUN curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain none
ENV PATH="/root/.cargo/bin:/root/.dotnet/tools:${PATH}"
RUN dotnet tool install --global PowerShell
COPY Lorekeeper.Press/rust-toolchain.toml /tmp/rust-toolchain.toml
RUN rustup toolchain install "$(grep -oP '^channel = "\K[^"]+' /tmp/rust-toolchain.toml)" --profile minimal
WORKDIR /src
COPY . .
# Publish inside /src because the Press packaging script requires its output to
# stay within the repository tree. Clearing CustomAfterDirectoryBuildTargets
# skips the Electron.NET packaging hook (npm/Electron downloads) that the
# browser-hosted server never uses.
RUN dotnet publish Lorekeeper/Lorekeeper.csproj -c Release -o /src/publish -p:CustomAfterDirectoryBuildTargets=

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# fontconfig backs SkiaSharp text rendering on Linux.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 lorekeeper \
    && useradd --system --uid 10001 --gid lorekeeper lorekeeper \
    && mkdir -p /data \
    && chown lorekeeper:lorekeeper /data
WORKDIR /app
COPY --from=build /src/publish .
USER lorekeeper
# /data holds the SQLite database, its protected migration backups, the Data
# Protection keys, and the local version-history Git repositories; mount a
# persistent volume there. Without the explicit history root the store would
# fall back to its development default — the content root's parent, which is
# the unwritable filesystem root inside the container. Auth__SingleUser__*
# secrets are supplied by the deployment, not baked into the image.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/lorekeeper.db" \
    Server__EnableHttpsRedirection=false \
    Server__DataProtectionKeysDirectory=/data/keys \
    VersionHistory__HistoryRoot=/data/history \
    XDG_CACHE_HOME=/tmp/cache
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Lorekeeper.dll"]
