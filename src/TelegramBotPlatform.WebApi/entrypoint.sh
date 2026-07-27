#!/bin/sh
set -e

# TelegramBotPlatform container entrypoint.
#
# - With an explicit command (e.g. docker-compose's `migrate` service passes ["migrate"]),
#   run exactly that and exit — keeps the existing compose workflow working.
# - With no command (the ECS Express Mode / default case), apply EF Core migrations first, then
#   start the app. Express Mode allows only one container, so migrate-then-serve runs here.
#   A failed migration exits non-zero -> the task never becomes healthy -> the canary keeps the
#   previous version live (FR-007). `exec` preserves SIGTERM for graceful shutdown.

if [ "$#" -gt 0 ]; then
  exec dotnet TelegramBotPlatform.WebApi.dll "$@"
fi

echo "Applying database migrations..."
dotnet TelegramBotPlatform.WebApi.dll migrate
echo "Migrations applied. Starting application..."
exec dotnet TelegramBotPlatform.WebApi.dll
