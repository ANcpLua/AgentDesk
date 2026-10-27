#!/bin/sh
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"
npm ci
npm run build:ui
dotnet publish AgentDesk/AgentDesk.csproj -c Release -o "$project_root/.local/runtime"
