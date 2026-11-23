# Демо в Yandex Cloud: запуск, остановка, обновление

## Ресурсы

| Ресурс | Значение |
|---|---|
| VM | `teammemory-demo` |
| Зона | `ru-central1-d` |
| Публичный IP | `81.26.181.247` (статический) |
| Домен Mini App | `https://81-26-181-247.sslip.io` |
| Webhook | `https://teammemory-webhook.grishuna0806.workers.dev/webhook` |
| SSH | `ssh -i ~/.ssh/id_ed25519 yc-user@81.26.181.247` |
| Security group | `teammemory-sg` (22, 80, 443, egress any) |
| Диск | 20 GB network-HDD |
| RAM / CPU | 4 GB / 2 vCPU |

Код на VM: `/home/yc-user/TeamMemory`, ветка `demo`.
Секреты: `/home/yc-user/TeamMemory/.env.production` (права `600`).

## Остановить на ночь

```powershell
yc compute instance stop --name teammemory-demo
```

Останавливаются CPU и RAM. Диск и статический IP продолжают тарифицироваться
(это небольшая сумма, но IP нужен, чтобы не изменился домен).

Проверить статус:

```powershell
yc compute instance get --name teammemory-demo
```

## Запустить перед показом

```powershell
yc compute instance start --name teammemory-demo
```

Дождаться `status: RUNNING` и проверить сервисы:

```powershell
ssh -i ~/.ssh/id_ed25519 yc-user@81.26.181.247 `
  "cd ~/TeamMemory && sudo docker compose --env-file .env.production -f docker-compose.production.yml ps"
```

Проверить HTTPS:

```powershell
curl.exe https://81-26-181-247.sslip.io/health
```

Если webhook накопились старые сообщения, сбросить очередь и зарегистрировать заново:

```powershell
$token = ((Get-Content .env | Where-Object { $_ -match '^\s*TELEGRAM_BOT_TOKEN\s*=' }) -split '=',2)[1].Trim()
Invoke-RestMethod "https://api.telegram.org/bot$token/deleteWebhook?drop_pending_updates=true"
ssh -i ~/.ssh/id_ed25519 yc-user@81.26.181.247 `
  "cd ~/TeamMemory && sudo docker compose --env-file .env.production -f docker-compose.production.yml run --rm --no-deps ingestion --register-telegram-webhook"
```

Открыть Mini App: личный чат с `@team_memory_bot` → кнопка меню «Открыть».

## Webhook через Cloudflare Worker

Сеть, в которой развёрнута VM, не пропускает входящие соединения от Telegram
(`getWebhookInfo` показывает `Connection timed out`). Поэтому webhook идёт через
Cloudflare Worker:

```text
Telegram -> https://teammemory-webhook.grishuna0806.workers.dev/webhook
         -> https://81-26-181-247.sslip.io/api/telegram/webhook
         -> Ingestion
```

Код Worker: [`deploy/cloudflare-worker.js`](../deploy/cloudflare-worker.js).
Worker публичный, но VM проверяет `X-Telegram-Bot-Api-Secret-Token`, поэтому
запрос без секрета получает `401`.

Преимущество: URL Worker не меняется при остановке/запуске VM. После старта VM
webhook уже настроен и перерегистрация не нужна.

Если Telegram накопил старые обновления за время простоя, перед показом можно
сбросить очередь через временный SSH SOCKS-прокси на отдельном сервере
(например, `ssh -N -D 127.0.0.1:1081 fkb`):

```powershell
$token = ((Get-Content .env | Where-Object { $_ -match '^\s*TELEGRAM_BOT_TOKEN\s*=' }) -split '=',2)[1].Trim()
curl.exe --socks5-hostname 127.0.0.1:1081 -X POST `
  "https://api.telegram.org/bot$token/deleteWebhook?drop_pending_updates=true"
```

Затем заново установить webhook на Worker (URL и секрет — из `.env.production`).

## Логи и диагностика

```powershell
ssh -i ~/.ssh/id_ed25519 yc-user@81.26.181.247 `
  "cd ~/TeamMemory && sudo docker compose --env-file .env.production -f docker-compose.production.yml logs -f dashboard analytics ingestion proxy"
```

## Обновить код

```powershell
ssh -i ~/.ssh/id_ed25519 yc-user@81.26.181.247 `
  "cd ~/TeamMemory && git pull && sudo env COMPOSE_PARALLEL_LIMIT=1 COMPOSE_BAKE=false docker compose --env-file .env.production -f docker-compose.production.yml build && sudo docker compose --env-file .env.production -f docker-compose.production.yml up -d"
```

## Полностью удалить

Удаление VM вместе с диском, затем IP и security group:

```powershell
yc compute instance delete --name teammemory-demo
yc vpc address delete --name teammemory-ip
yc vpc security-group delete --name teammemory-sg
```

После удаления статического IP домен `81-26-181-247.sslip.io` перестанет работать.

## Стоимость

- `stop` экономит CPU/RAM; диск и IP остаются в тарифе.
- Для минимальной стоимости после показа можно удалить VM, IP и security group.
- Если нужно пересобрать образы, временно поднимите долю CPU:

```powershell
yc compute instance update --name teammemory-demo --core-fraction 100
# после сборки
yc compute instance update --name teammemory-demo --core-fraction 20
```
