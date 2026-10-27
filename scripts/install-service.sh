#!/bin/sh
# Install/update a per-user macOS service. No root privileges are needed.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
runtime_dir="$project_root/.local/runtime"
dotnet_path=${QUESTION_INBOX_DOTNET:-$(command -v dotnet)}
service_label=local.agentdesk
service_domain="gui/$(id -u)"
plist_path="$HOME/Library/LaunchAgents/$service_label.plist"
if [ ! -f "$runtime_dir/AgentDesk.dll" ]; then
  echo 'Build first with scripts/build.sh.' >&2
  exit 1
fi
mkdir -p "$HOME/Library/LaunchAgents" "$project_root/.local/logs"
python3 - "$plist_path" "$dotnet_path" "$runtime_dir" "$project_root/.local/logs" <<'PY'
import plistlib,sys
path,dotnet,runtime,logs=sys.argv[1:]
data={
    'Label':'local.agentdesk',
    'ProgramArguments':[dotnet,runtime+'/AgentDesk.dll','--urls','http://127.0.0.1:5137'],
    'WorkingDirectory':runtime,
    'RunAtLoad':True,
    'KeepAlive':True,
    'StandardOutPath':logs+'/service.log',
    'StandardErrorPath':logs+'/service-error.log',
    'ProcessType':'Background',
    'ThrottleInterval':5
}
with open(path,'wb') as file: plistlib.dump(data,file)
PY
launchctl bootout "$service_domain/$service_label" 2>/dev/null || true
# launchd can finish unloading asynchronously. Give the old registration time to disappear.
attempt=0
while launchctl print "$service_domain/$service_label" >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 50 ]; then
    echo 'The previous inbox service has not finished stopping.' >&2
    exit 1
  fi
  sleep 0.2
done
attempt=0
until launchctl bootstrap "$service_domain" "$plist_path" 2>"$project_root/.local/logs/bootstrap.log"; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 30 ]; then
    cat "$project_root/.local/logs/bootstrap.log" >&2
    exit 1
  fi
  sleep 0.2
done
echo 'Question inbox installed as a login service: http://127.0.0.1:5137'
