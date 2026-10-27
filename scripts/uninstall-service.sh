#!/bin/sh
set -eu
service_label=local.agentdesk
launchctl bootout "gui/$(id -u)/$service_label" 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/$service_label.plist"
echo 'Login service removed. Stored questions and project files remain unchanged.'
