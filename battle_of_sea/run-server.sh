#!/bin/bash
# Запуск сервера (через dotnet exec обходим "Operation not permitted" на macOS)
cd "$(dirname "$0")/battle_of_sea"
dotnet build -q
exec dotnet exec ./bin/Debug/net9.0/battle_of_sea.dll
