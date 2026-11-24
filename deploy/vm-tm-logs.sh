#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

tm() { sudo docker compose --env-file .env.production -f docker-compose.production.yml "$@"; }

case "${1:-}" in
  ingestion)
    tm logs --tail=50 -f ingestion
    ;;
  analytics)
    tm logs --tail=50 -f analytics \
      | grep --line-buffered -E "Received TelegramMessageCreated|Applied batch|requires review|HTTP request|HTTP response"
    ;;
  dashboard)
    tm logs --tail=50 -f dashboard \
      | grep --line-buffered "Received"
    ;;
  *)
    echo "usage: $0 ingestion|analytics|dashboard" >&2
    exit 1
    ;;
esac
