# Запуск всего проекта в Docker

Нужен Docker Desktop в режиме Linux containers. Локальные .NET SDK и Node.js не нужны:
backend и frontend собираются внутри контейнеров.

В корневом `.env` укажите токен бота и ключ LLM API:

```dotenv
TELEGRAM_BOT_TOKEN=ваш_токен
LlmApi__ApiKey=ваш_ключ
WEBHOOK_BASE_URL=
MINIAPP_DEVELOPMENT_MANAGER_ID=123456789
TELEGRAM_WEBHOOK_SECRET=
ANALYTICS_POSTGRES_PORT=5434
DEMO_GROUP_CHAT_ID=-1001234567890
DEMO_GROUP_TITLE=Demo Team
COMPOSE_PARALLEL_LIMIT=1
COMPOSE_BAKE=false
```

`ChatId` автоматически приходит от Ingestion в каждом сообщении. Analytics не требует
ручной настройки чатов или идентификатора команды. `MINIAPP_DEVELOPMENT_MANAGER_ID`
нужен только для локального входа в браузере без Telegram; настоящая Mini App использует initData.
Существующие секреты в `.env` сохраняйте; файл не входит в Docker build context.
Analytics использует OpenAI-совместимый HTTP API (`LlmApi__*`), локальная GGUF-модель не нужна.

Из корня репозитория:

```powershell
docker compose up -d --build
docker compose ps
```

`database-init` — одноразовый контейнер, поэтому `--wait` на всём проекте завершается
с ненулевым кодом после его успешного выхода. Дождитесь статуса `healthy` у
`dashboard`, `ingestion`, `frontend`, `postgres` и `rabbitmq`.

Первый запуск скачивает базовые образы и зависимости и занимает больше времени;
следующие сборки используют Docker cache.

Compose поднимает PostgreSQL, RabbitMQ, создаёт недостающие базы `analytics` и
`group_dashboard`, запускает Dashboard, Analytics, Ingestion и frontend. При повторном
запуске сохраняются существующие базы и данные.

Адреса:

- Mini App / frontend: http://localhost:5173
- Dashboard health: http://localhost:5080/health
- Ingestion health и webhook: http://localhost:5081/health, `/api/telegram/webhook`
- RabbitMQ UI: http://localhost:15672, пользователь/пароль `teammemory` / `teammemory`
- PostgreSQL с хоста: `localhost:5434`, пользователь/пароль `teammemory` / `teammemory`

Frontend раздаётся nginx, который проксирует `/api` и `/hubs` в Dashboard, включая
WebSocket для SignalR. Все сервисы внутри Docker используют `postgres:5432` и `rabbitmq:5672`.
Не запускайте одновременно локальные экземпляры приложений на тех же портах.

## Демо-сценарий

Dashboard в Development создаёт демонстрационную группу `DEMO_GROUP_CHAT_ID` и привязывает
её к `MINIAPP_DEVELOPMENT_MANAGER_ID`. Отправьте тестовое сообщение от имени сотрудника:

```powershell
Invoke-RestMethod -Method Post http://localhost:5081/api/dev/telegram/messages `
  -ContentType "application/json" `
  -Body '{"chatId":-1001234567890,"messageId":1,"senderId":42,"senderUsername":"ivan","text":"Сделаю отчёт к пятнице"}'
```

Analytics подождёт `Analytics__IdleSeconds` (60 секунд), вызовет LLM и запишет задачи.
После этого:

- `GET http://localhost:5080/api/groups` — список групп (в Development доступны все);
- `GET http://localhost:5080/api/groups/-1001234567890/digest` — дайджест;
- http://localhost:5173 — Mini App; в браузере вход выполняется под Development-менеджером;
- `docker compose logs -f analytics dashboard` — обработка и события.

Проверить данные в PostgreSQL:

```powershell
docker compose exec postgres psql -U teammemory -d analytics -c "select id, title, status from tasks;"
docker compose exec postgres psql -U teammemory -d group_dashboard -c "select * from analytics_events;"
```

Диагностика и остановка:

```powershell
docker compose ps
docker compose logs -f analytics dashboard ingestion
docker compose down
```

Обычный `down` сохраняет тома с данными. `down --volumes` удаляет эти данные.

Если `WEBHOOK_BASE_URL` пуст, Ingestion пропускает регистрацию Telegram webhook и
доступен для тестового `POST /api/dev/telegram/messages`. Для настоящих сообщений
запустите `ngrok http 5081`, задайте публичный HTTPS URL в `.env` и пересоздайте Ingestion:

```powershell
docker compose up -d ingestion
```

Конфигурация предназначена для локальной разработки: используется Development-режим
и тестовый вход `MiniApp:DevelopmentManagerId`. Пользователь `123456789` в Development
видит все группы. В Production вход выполняется через подписанный Telegram `initData`,
а группы должны быть зарегистрированы через `groups.updated.v1` / `group_managers`.

Деплой на сервер: [deployment.md](deployment.md).
