#!/bin/sh
# This script's stdout belongs exclusively to the MCP protocol.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
runtime_dir="$project_root/.local/runtime"
inbox_url=${QUESTION_INBOX_URL:-http://127.0.0.1:5137}
dotnet_path=${QUESTION_INBOX_DOTNET:-}
if [ -z "$dotnet_path" ]; then
  dotnet_path=$(command -v dotnet || true)
fi
if [ -z "$dotnet_path" ] && [ -x /usr/local/share/dotnet/dotnet ]; then
  dotnet_path=/usr/local/share/dotnet/dotnet
fi
if [ -z "$dotnet_path" ] || [ ! -f "$runtime_dir/AgentDesk.dll" ]; then
  echo 'Build the inbox first with scripts/build.sh. A .NET 11 runtime is required.' >&2
  exit 1
fi
probe() {
  health=$(curl -fsS --max-time 1 "$inbox_url/health" 2>/dev/null) || return 1
  case "$health" in *'"service":"question-inbox"'*) return 0;; *) return 1;; esac
}
if ! probe; then
  mkdir -p "$project_root/.local/logs"
  nohup "$dotnet_path" "$runtime_dir/AgentDesk.dll" --urls "$inbox_url" \
    >"$project_root/.local/logs/server-$$.log" 2>&1 </dev/null &
  started_pid=$!
  attempt=0
  until probe; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 30 ]; then
      echo "Question inbox did not start. Check $project_root/.local/logs and whether $inbox_url is occupied." >&2
      exit 1
    fi
    sleep 0.2
  done
  if kill -0 "$started_pid" 2>/dev/null; then
    printf '%s\n' "$started_pid" > "$project_root/.local/server.pid"
  fi
fi
export QUESTION_INBOX_URL="$inbox_url"
export QUESTION_INBOX_CONTENT_ROOT="$runtime_dir"
exec "$dotnet_path" "$runtime_dir/AgentDesk.dll" --mcp-stdio
