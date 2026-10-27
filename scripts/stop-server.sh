#!/bin/sh
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
if command -v launchctl >/dev/null 2>&1 && launchctl print "gui/$(id -u)/local.agentdesk" >/dev/null 2>&1; then
  launchctl bootout "gui/$(id -u)/local.agentdesk"
  echo 'Login service stopped for this login session. Run install-service.sh to restart it.'
  exit 0
fi
pid_file="$project_root/.local/server.pid"
if [ ! -f "$pid_file" ]; then
  echo 'No managed inbox server PID found.'
  exit 0
fi
server_pid=$(cat "$pid_file")
server_command=$(ps -p "$server_pid" -o command= || true)
case "$server_command" in
  *"$project_root/.local/runtime/AgentDesk.dll"*) kill "$server_pid"; rm "$pid_file"; echo 'Inbox server stopped. Questions remain in SQLite.';;
  *) echo 'PID no longer belongs to this inbox; no process was stopped.'; rm "$pid_file";;
esac
