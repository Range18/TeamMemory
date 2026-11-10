# Запуск всего проекта в Docker

Нужен Docker Desktop в режиме Linux containers. Локальные .NET SDK и Node.js не нужны:
backend и frontend собираются внутри контейнеров.

В корневом `.env` укажите настоящий токен бота и путь к скачанному GGUF-файлу:

```dotenv
TELEGRAM_BOT_TOKEN=ваш_токен
Llama__ModelPath=E:/FIIT/models/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf
WEBHOOK_BASE_URL=
MINIAPP_DEVELOPMENT_MANAGER_ID=123456789
POSTGRES_PORT=5434
COMPOSE_PARALLEL_LIMIT=1
COMPOSE_BAKE=false
```

ChatId автоматически приходит от Ingestion в каждом сообщении. Analytics не требует
ручной настройки чатов или идентификатора команды. `MINIAPP_DEVELOPMENT_MANAGER_ID`
нужен только для локального входа в браузере без Telegram; настоящая Mini App использует initData.
Существующие секреты в `.env` сохраняйте; файл не входит в Docker build context.
Модель подключается только для чтения по пути `/models/model.gguf` внутри Analytics.
Переменная `Llama__ModelPath` в `.env` при этом остаётся путём к файлу на вашем компьютере.

Из корня репозитория:

```powershell
docker compose up -d --build --wait
```

Первый запуск скачивает базовые образы и зависимости и занимает больше времени;
следующие сборки используют Docker cache.

Compose поднимает PostgreSQL, RabbitMQ, создаёт недостающие базы `analytics` и
`group_dashboard`, запускает Dashboard, Analytics, Ingestion и frontend. При повторном
запуске сохраняются существующие базы и данные. Health checks не запускают inference;
модель загружается при первом анализе сообщений.

Адреса:

- Mini App / frontend: http://localhost:5173
- Dashboard health: http://localhost:5080/health
- Ingestion health и webhook: http://localhost:5081/health, `/api/telegram/webhook`
- RabbitMQ UI: http://localhost:15672, пользователь/пароль `teammemory` / `teammemory`
- PostgreSQL с хоста: `localhost:5434`, пользователь/пароль `teammemory` / `teammemory`

Frontend раздаётся nginx, который проксирует `/api` и `/hubs` в Dashboard, включая
WebSocket для SignalR. Все сервисы внутри Docker используют `postgres:5432` и `rabbitmq:5672`.
Не запускайте одновременно локальные экземпляры приложений на тех же портах.

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
и существующий тестовый вход `MiniApp:DevelopmentManagerId`. Чтобы видеть данные в
интерфейсе, группа должна быть зарегистрирована и связана с руководителем через
`GroupUpdatedMessage`, как описано в [README Dashboard](../src/DashboardService/README.md).
