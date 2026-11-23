# Деплой TeamMemory через Docker Compose

Конфигурация рассчитана на один Linux-сервер, Docker Engine с Compose v2+,
публичный DNS-домен и прямой доступ к серверу по TCP 80/443. Сборка выполняется
на сервере, registry не требуется. .NET SDK на сервере не нужен: он находится
в build-стадиях образов. Это установка без высокой доступности; обновление
сервисов может кратковременно прервать приём webhook.

Production использует **только** `docker-compose.production.yml`. Не объединяйте
его с локальным `docker-compose.yml`: иначе можно опубликовать лишние порты.
Имя Compose-проекта фиксировано: `teammemory-production`; от него зависят имена
volumes. Локальные данные автоматически в production не переносятся.

## Подготовка

1. Установите Docker Engine и Compose plugin из официального репозитория Docker
   для выбранного Linux-дистрибутива. Проверьте `docker version` и
   `docker compose version`. Пользователь деплоя должен иметь доступ к Docker.
2. Скопируйте репозиторий на сервер и перейдите в его корень.
3. Создайте DNS A-запись `<BOT_DOMAIN>` на публичный IPv4 сервера. Добавляйте
   AAAA только при рабочем IPv6. Разрешите входящие TCP 80/443 в firewall сервера
   и облачного провайдера. Исходящие DNS/HTTPS нужны для ACME, Telegram и LLM.
4. Создайте конфигурацию:

```bash
umask 077
cp .env.production.example .env.production
chmod 600 .env.production
```

Заполните `.env.production` редактором:

- `APP_DOMAIN=<BOT_DOMAIN>` — реальный домен, без `https://`, пути и порта.
- `ACME_EMAIL=<OPERATIONS_EMAIL>` — адрес для ACME.
- `APP_IMAGE_TAG=<RELEASE_TAG>` — уникальный Docker tag релиза, например `release-001`.
- Пароли PostgreSQL и RabbitMQ — разные случайные значения; сгенерируйте каждый
  командой `openssl rand -hex 32`. Для пароля PostgreSQL используйте hex, чтобы
  не требовалось экранирование внутри строки подключения.
- `TELEGRAM_BOT_TOKEN` — токен BotFather; `TELEGRAM_WEBHOOK_SECRET` — ещё одно
  случайное hex-значение. Секрет webhook проверяется приложением.
- `LlmApi__Endpoint`, `LlmApi__Model`, `LlmApi__ApiKey` — параметры доступного
  вам OpenAI-совместимого API. Значения endpoint/model в шаблоне взяты из
  локального примера проекта; доступность модели проверьте у своего провайдера.

Не выполняйте `source .env.production`: файл предназначен для Compose, а не
для shell. Не используйте `set -x` и не публикуйте вывод `docker inspect` или
полного `docker compose config`: они могут содержать секреты. Переменные
передаются приложениям явно через `environment`, файл не копируется в образ.
Доступ администратора Docker позволяет прочитать эти переменные.

Все дальнейшие команды выполняются из корня репозитория в Bash. Определите
функцию заново при открытии новой сессии:

```bash
set -euo pipefail
dc() { docker compose --env-file .env.production -f docker-compose.production.yml "$@"; }
dc config --quiet
dc build --pull
```

`config --quiet` проверяет структуру и обязательные значения без вывода
развёрнутой конфигурации. Пустые обязательные секреты приводят к ошибке.

## Первый запуск

Сначала поднимите потребителей, чтобы их очереди существовали до прихода событий:

```bash
dc up -d --wait postgres rabbitmq
dc up -d dashboard analytics
dc logs --tail=100 analytics dashboard
dc exec rabbitmq rabbitmqctl list_queues name consumers messages_ready messages_unacknowledged
dc exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "\dt"'
dc exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d group_dashboard -c "\dt"'
```

Перед следующим шагом убедитесь, что Analytics не перезапускается, в логах есть
старт bus/receive endpoint, а очереди имеют потребителей. В БД должны появиться
`inbox_events`, `analysis_batches`, `tasks`, `task_changes`, `outbox_messages`, а в
`group_dashboard` — `groups`, `group_managers`, `analytics_events`, `digest_notifications`.
При нестандартном `RABBITMQ_VHOST` добавляйте `-p <VHOST>` к `rabbitmqctl`.

Analytics — Worker без HTTP-сервера и фиктивного healthcheck. Статус `running`
и `up --wait` для него не означают готовность обработки: нужны проверки выше.
Web-сервисы имеют `/health/live` и `/health/ready`; readiness включает проверку
MassTransit. Эти адреса доступны только внутри Docker-сети.

```bash
dc up -d --wait ingestion proxy
dc logs --tail=100 proxy
curl --silent --output /dev/null --write-out '%{http_code}\n' https://<BOT_DOMAIN>/
curl --silent --output /dev/null --write-out '%{http_code}\n' https://<BOT_DOMAIN>/health
```

Ожидается HTTPS-ответ `200` для frontend и `/health` Dashboard с валидным сертификатом.
Caddy получает и продлевает сертификат автоматически; состояние ACME хранится в
`caddy-data`. Не обходите проверку сертификата через `curl -k`. Caddy маршрутизирует
`POST /api/telegram/webhook` в Ingestion, `/api/*` и `/hubs/*` — в Dashboard, остальное —
в frontend. PostgreSQL, RabbitMQ и его Management UI не имеют опубликованных портов.

Теперь явно зарегистрируйте webhook:

```bash
dc run --rm --no-deps ingestion --register-telegram-webhook
```

Команда использует конфигурацию контейнера, передаёт Telegram секрет заголовка,
не запускает HTTP-сервер и RabbitMQ bus и завершается с ненулевым кодом при ошибке.
Она не очищает ожидающие обновления. Повторите её при смене домена, бота или
секрета webhook. Не включайте Development в production. Автоматическая
регистрация и ngrok остаются инструментами локальной разработки.

## Проверка сценария и диагностика

Добавьте бота в тестовую Telegram-группу; настройте privacy mode через BotFather
или права администратора согласно локальному README. Отправьте новое сообщение
с конкретной задачей, исполнителем и сроком. Проверьте:

```bash
dc ps
dc logs --since=10m --tail=200 ingestion analytics dashboard
dc exec rabbitmq rabbitmqctl list_queues name consumers messages_ready messages_unacknowledged
dc exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT event_id, chat_id, message_id, received_at, batch_id
FROM inbox_events ORDER BY received_at DESC LIMIT 10;
SELECT id, status FROM analysis_batches ORDER BY created_at DESC LIMIT 10;
SELECT count(*) AS tasks_count FROM tasks;
SELECT event_id, event_type, published_at
FROM outbox_messages ORDER BY created_at DESC LIMIT 10;
SQL
dc exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d group_dashboard' <<'SQL'
SELECT chat_id, title, status FROM groups;
SELECT count(*) AS analytics_events_count FROM analytics_events;
SELECT count(*) FILTER (WHERE sent_at_utc IS NULL) AS pending_notifications
FROM digest_notifications;
SQL
```

Сопоставьте `EventId` Ingestion и Analytics с записью inbox. Подождите
`Analytics__IdleSeconds` после последнего сообщения и время вызова LLM. При
распознанном изменении задачи ожидаются `Applied batch`, данные tasks/outbox и
событие в логах Dashboard. Не каждое произвольное сообщение создаёт задачу.
Проверьте отсутствие роста error-очередей, статусов `failed`/`needs_review`
и неопубликованных outbox. Содержимое логов может включать рабочие сообщения:
ограничивайте доступ к нему.

Внешний POST без секретного заголовка должен возвращать `401`:

```bash
curl --silent --output /dev/null --write-out '%{http_code}\n' \
  -X POST -H 'Content-Type: application/json' -d '{}' \
  https://<BOT_DOMAIN>/api/telegram/webhook
```

Веб-проверки можно выполнить из контейнеров:

```bash
dc exec ingestion curl --fail --silent http://127.0.0.1:8080/health/ready
dc exec dashboard curl --fail --silent http://127.0.0.1:8080/health/ready
```

`depends_on` управляет начальным запуском, а не восстановлением после сбоев.
MassTransit повторно подключается к RabbitMQ; consumers уже имеют message retry.
Циклы AnalysisWorker и OutboxPublisher обрабатывают ошибки БД и повторяют работу.
Ошибка инициализации БД завершает Analytics; `restart: unless-stopped` запускает
его повторно. Healthcheck сам по себе контейнер не перезапускает. Настройте
внешний мониторинг рестартов, очередей, ошибок и возраста неопубликованного outbox.
Допустимы повторные доставки; абсолютная гарантия отсутствия потерь не заявляется.

## Схема БД и обновление

`AnalyticsStore.Initialize` использует PostgreSQL advisory lock и
`EnsureCreatedAsync`: пустая БД создаётся из EF-модели, полная существующая схема
не пересоздаётся. Автоматических EF-миграций здесь нет. Частичная или устаревшая
схема автоматически не исправляется. Не используйте `EnsureDeleted`, удаление
volumes или пересоздание БД для обновления приложения.

Перед обновлением сделайте backup по следующему разделу. Сохраните предыдущие
исходники, тег образов и закрытую копию `.env.production`. Для следующего релиза
получите нужную ревизию кода и задайте новый `APP_IMAGE_TAG`, затем:

```bash
dc config --quiet
dc build --pull
dc up -d
dc ps
dc logs --since=5m --tail=100 analytics ingestion dashboard
```

Не перезаписывайте старые теги и не выполняйте image prune до окончания периода
отката. Для неизменной/совместимой схемы откат: восстановите предыдущую версию
Compose и конфигурации, выставьте прежний `APP_IMAGE_TAG`, выполните
`dc up -d --no-build --pull never --wait`. Проверьте сценарий повторно.
При изменении схемы сначала разработайте и проверьте отдельную SQL-миграцию
и план совместимости; один откат образов схему не откатывает. Восстановление
backup возвращает состояние на момент копирования, включая потерю более новых
изменений. Пароли PostgreSQL/RabbitMQ из env применяются при первой инициализации
volume; их изменение в файле не меняет пользователей в существующих данных.

## Резервное копирование

Для согласованной копии остановите вход и обработку; Telegram временно будет
повторять доставку. Пример создаёт набор с уникальным именем. Выполняйте все
команды в одной Bash-сессии; продолжайте только при успешном завершении каждой.

```bash
umask 077
backup_set="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "backups/$backup_set"
dc stop proxy ingestion analytics dashboard rabbitmq
dc exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' \
  > "backups/$backup_set/analytics.dump"
dc exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d group_dashboard -Fc' \
  > "backups/$backup_set/dashboard.dump"
docker run --rm --volumes-from "$(dc ps -a -q rabbitmq)" \
  -v "$PWD/backups/$backup_set:/backup" --entrypoint tar \
  rabbitmq:4.4-management -C /var/lib/rabbitmq -czf /backup/rabbitmq.tgz .
cp .env.production "backups/$backup_set/env.production"
dc up -d --wait postgres rabbitmq
dc up -d dashboard analytics
```

Проверьте очереди/Analytics как при первом запуске, затем `dc up -d --wait ingestion proxy`.
Если копирование завершилось ошибкой, не считайте набор пригодным; устраните
ошибку и отдельно восстановите работу сервисов. Храните вместе с набором ревизию
кода, теги/digests и сами образы приложения (registry или `docker image save`).
Скопируйте набор на отдельное защищённое хранилище, шифруйте его и регулярно
проверяйте восстановление. PostgreSQL dump не содержит сообщений RabbitMQ:
для брокера нужна именно остановленная копия данных. Caddy может получить
сертификаты заново; при необходимости отдельно сохраните `caddy-data`.

## Восстановление на чистом сервере

Используйте ту же версию PostgreSQL, RabbitMQ и приложения, фиксированное имя
проекта и hostname брокера. Это процедура **для пустых volumes**: не распаковывайте
архив RabbitMQ поверх работающего или уже заполненного брокера. Существующую
установку сначала сохраните отдельно. Не запускайте два экземпляра с одним ботом.

Восстановите репозиторий, закрытый env-файл и образы, задайте путь к набору:

```bash
backup_dir="$PWD/backups/<BACKUP_SET>"
cp "$backup_dir/env.production" .env.production
chmod 600 .env.production
dc config --quiet
dc up -d --wait postgres
dc run --rm --no-deps database-init
dc exec -T postgres sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges --exit-on-error' \
  < "$backup_dir/analytics.dump"
dc exec -T postgres sh -c 'pg_restore -U "$POSTGRES_USER" -d group_dashboard --no-owner --no-privileges --exit-on-error' \
  < "$backup_dir/dashboard.dump"
dc create rabbitmq
docker run --rm --volumes-from "$(dc ps -a -q rabbitmq)" \
  -v "$backup_dir:/backup:ro" --entrypoint tar \
  rabbitmq:4.4-management -C /var/lib/rabbitmq -xzf /backup/rabbitmq.tgz
dc up -d --wait postgres rabbitmq
dc up -d dashboard analytics
```

Проверьте БД, очереди и запуск Analytics, затем:

```bash
dc up -d --wait ingestion proxy
dc run --rm --no-deps ingestion --register-telegram-webhook
```

Повторите проверку сценария. Volumes переживают `dc down`; **`dc down -v`
удаляет данные и не является командой обновления**.

## Локальная разработка и проверки

Локальный `docker-compose.yml` поднимает весь проект с привязкой портов к loopback.
Только инфраструктуру для запуска сервисов через `dotnet run` можно поднять так:

```bash
docker compose --env-file .env.example -f docker-compose.yml up -d postgres rabbitmq database-init
```

Проверки на машине с .NET SDK 10 и Docker:

```bash
dotnet build TeamMemory.sln
dotnet run --project tests/AnalyticsService.Checks -- --analysis-only
dotnet run --project tests/AnalyticsService.Checks -- --llm-api-only
docker compose -f tests/AnalyticsService.Checks/docker-compose.yml up -d postgres
dotnet run --project tests/AnalyticsService.Checks
```

Интеграционные проверки создают отдельную временную БД; подробности находятся
в `tests/AnalyticsService.Checks/README.md`.

При подготовке конфигурации проверены:

- `dotnet build TeamMemory.sln` — 0 ошибок;
- `--analysis-only` — 4/4 PASS;
- полный прогон `AnalyticsService.Checks` с PostgreSQL — все проверки PASS;
- `npm ci && npm run build` во `frontend`;
- `docker compose config` для локальной и production-модели;
- сборка всех четырёх Docker-образов (`analytics`, `dashboard`, `ingestion`, `frontend`);
- локальный smoke Dashboard: `/health`, Development-вход, список групп, дайджест и
  приём реального `NewTaskReported` из RabbitMQ с записью в `analytics_events`.

Не проверялись в этом окружении: реальный Telegram webhook, вызов внешнего LLM,
Caddy/ACME на публичном домене и восстановление backup. Их нужно проверить на
сервере по шагам выше.

Официальные справочники: [порядок запуска Compose](https://docs.docker.com/compose/how-tos/startup-order/),
[reverse proxy Caddy](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy),
[образы .NET](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images).
