# Контекст сборки — корень репозитория (в docker-compose.yml: context: .).
# Server ссылается на проекты Core и Security, поэтому их исходники должны попадать в контекст.
# ---------- Сборка ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# слой restore: csproj-файлы редко меняются — NuGet-пакеты кэшируются
COPY Server/Server.csproj Server/
COPY Core/SecureChat.Core.csproj Core/
COPY Security/SecureChat.Security.csproj Security/
RUN dotnet restore Server/Server.csproj

COPY Server/ Server/
COPY Core/ Core/
COPY Security/ Security/
RUN dotnet publish Server/Server.csproj -c Release -o /app/publish --no-restore

# ---------- Рантайм ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# curl для HEALTHCHECK (в aspnet-образе его нет — без него контейнер вечно unhealthy)
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# БД (server.db + WAL), логи и аватары пишутся в CWD процесса:
# в docker-compose CWD = /data (том), конфиг читается из /app (ContentRoot зафиксирован в Program.cs).
EXPOSE 9000

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/bin/sh", "-c", "curl -fsS http://localhost:9000/health || exit 1"]

# абсолютный путь: CWD контейнера = /data (том данных), dll живёт в /app
ENTRYPOINT ["dotnet", "/app/Server.dll"]
