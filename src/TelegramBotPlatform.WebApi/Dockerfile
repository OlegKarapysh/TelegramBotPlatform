FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Copy the full source before restore: the WebApi project references the other
# projects, and Central Package Management adds root-level Directory.*.props that
# `dotnet restore` requires.
COPY . .
RUN dotnet restore "src/TelegramBotPlatform.WebApi/TelegramBotPlatform.WebApi.csproj"
WORKDIR "/src/src/TelegramBotPlatform.WebApi"
RUN dotnet publish "./TelegramBotPlatform.WebApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# curl backs the container healthcheck (see docker-compose.prod.yml).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

# Migrate-then-serve entrypoint. It runs an explicit command as-is (so docker-compose's `migrate`
# service still works) and, when given no command, applies EF migrations before starting the app —
# required on ECS Express Mode, which allows only one container (no init/sidecar).
COPY src/TelegramBotPlatform.WebApi/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# The effective listen port is set at runtime via ASPNETCORE_URLS.
# EXPOSE is informational only.
ARG APP_PORT=8080
EXPOSE ${APP_PORT}
ENTRYPOINT ["/app/entrypoint.sh"]
