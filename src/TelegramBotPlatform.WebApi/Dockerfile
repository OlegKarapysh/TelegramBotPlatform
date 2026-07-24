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

# The effective listen port is set at runtime via ASPNETCORE_URLS in docker-compose.prod.yml.
# EXPOSE is informational only.
ARG APP_PORT=8080
EXPOSE ${APP_PORT}
ENTRYPOINT ["dotnet", "TelegramBotPlatform.WebApi.dll"]
