# syntax=docker/dockerfile:1
# Weave CoWork server: bun builds the web app, dotnet publishes the server, a non-root user runs it.

FROM docker.io/oven/bun:1 AS web
WORKDIR /src/web
COPY web/package.json web/bun.lock* ./
RUN bun install --frozen-lockfile || bun install
COPY web/ ./
# vite.config.ts writes to ../src/CoWork.Server/wwwroot
RUN bun run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CoWork.slnx ./
COPY src/CoWork.Contracts/*.csproj src/CoWork.Contracts/
COPY src/CoWork.Server/*.csproj src/CoWork.Server/
RUN dotnet restore src/CoWork.Server/CoWork.Server.csproj
COPY src/CoWork.Contracts/ src/CoWork.Contracts/
COPY src/CoWork.Server/ src/CoWork.Server/
COPY --from=web /src/src/CoWork.Server/wwwroot/ src/CoWork.Server/wwwroot/
RUN dotnet publish src/CoWork.Server/CoWork.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN useradd --system --uid 10001 --create-home cowork && mkdir -p /data && chown cowork /data
USER cowork
ENV COWORK_DATA=/data ASPNETCORE_URLS=http://0.0.0.0:5880
VOLUME /data
EXPOSE 5880
ENTRYPOINT ["dotnet", "CoWork.Server.dll"]
