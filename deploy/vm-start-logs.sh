#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
root="$(pwd)"

tmux kill-session -t logs 2>/dev/null || true

tmux new-session -d -s logs -c "$root" "bash '$root/deploy/vm-tm-logs.sh' ingestion"
tmux split-window -h -t logs -c "$root" "bash '$root/deploy/vm-tm-logs.sh' analytics"
tmux split-window -v -t logs -c "$root" "bash '$root/deploy/vm-tm-logs.sh' dashboard"
tmux select-pane -t logs:0.0
tmux select-layout -t logs main-vertical

echo "tmux session 'logs' started"
echo "attach: tmux attach -t logs"
