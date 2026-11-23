# AI-Агент для анализа активности команды

> **Кратко:** TeamMemory собирает события рабочих чатов через RabbitMQ, анализирует их через LLM API, сохраняет задачи в PostgreSQL и передаёт Dashboard события новых задач, блокеров и риска. Сейчас реализован основной Telegram-сценарий.

## О проекте

Проект состоит из трёх .NET-микросервисов: Ingestion принимает события, Analytics обрабатывает их,
Dashboard показывает группы, задачи и дайджесты руководителю через Telegram Mini App.
Analytics сохраняет Telegram-события в PostgreSQL, анализирует группы сообщений через
OpenAI-совместимый HTTP API модели и публикует события задач, блокеров и риска для Dashboard.
Запуск описан в [документации Analytics](src/AnalyticsService/README.md) и
[инструкции Docker](docs/docker-run.md).
GitHub-коннекторы — в планах.

## Архитектура

```mermaid
flowchart LR
    T[Telegram] --> I[Ingestion Service]
    G[GitHub] -.-> I
    I -->|RabbitMQ / MassTransit| A[Analytics Service]
    A --> D[Dashboard Service]
    D --> U[Руководитель]
```

- **Ingestion** — Telegram webhook, публикация `TelegramMessageCreated`.
- **Analytics** — Telegram inbox, групповой LLM-анализ, задачи и история в PostgreSQL, outbox.
- **Dashboard** — подписки на `NewTaskReported`, `BlockerDetected`, `TaskAtRisk`, хранение событий,
  дайджесты, SignalR и REST API для React Mini App.

## Структура

```text
src/IngestionService/     # webhook Telegram, публикация событий
src/AnalyticsService/     # консьюмеры RabbitMQ
src/DashboardService/     # REST API, SignalR, дайджесты
frontend/                 # React Mini App
shared/Shared.Contracts/  # контракты событий
.env.example              # шаблон локальных переменных
docker-compose.yml        # все сервисы, PostgreSQL и RabbitMQ
```

## Требования

- .NET SDK 10.0.x
- Docker и Docker Compose
- ngrok
- Telegram-бот от @BotFather, добавленный в группу
- свободные порты `5080`, `5081`, `5173`, `5434`, `5672`, `15672` (`4040` — инспектор ngrok)

## Переменные окружения

`.env` лежит в корне и не коммитится. Шаблон — `.env.example`:

```powershell
Copy-Item .env.example .env
```

| Переменная | Назначение |
| --- | --- |
| `TELEGRAM_BOT_TOKEN` | Токен бота от @BotFather |
| `WEBHOOK_BASE_URL` | Личный ngrok-адрес, например `https://your-name.ngrok-free.dev` |
| `LlmApi__Endpoint`, `LlmApi__Model` | Endpoint и модель OpenAI-совместимого LLM API; в шаблоне — DeepSeek |
| `LlmApi__ApiKey` | API-ключ LLM |
| `ConnectionStrings__Analytics` | Строка подключения Analytics к PostgreSQL |
| `ConnectionStrings__Postgres` | Строка подключения Dashboard к PostgreSQL |
| `MINIAPP_DEVELOPMENT_MANAGER_ID` | Development-пользователь для входа в браузере без Telegram |
| `TELEGRAM_WEBHOOK_SECRET` | Необязательный секрет заголовка Telegram webhook |
| `DEMO_GROUP_CHAT_ID`, `DEMO_GROUP_TITLE` | Демо-группа, которую Dashboard создаёт в Development |
| `RabbitMq__*` | Подключение приложения к RabbitMQ |
| `Analytics__ChatTeams__<ChatId>` | Необязательное переопределение команды; по умолчанию используется ID чата |
| `Analytics__*`, `LlmApi__*` | Интервалы, лимиты и остальные параметры; полный набор в `.env.example` |

`WEBHOOK_BASE_URL` указывается без завершающего `/`. Ingestion и Analytics загружают `.env` при старте. Переменные ОС имеют приоритет над `.env`. После изменения файла сервис нужно перезапустить. Подробности настройки LLM — в [README Analytics](src/AnalyticsService/README.md).

## Telegram и ngrok

1. Создайте бота в @BotFather и добавьте токен в `.env`.
2. Добавьте бота в группу и отключите privacy mode (`/setprivacy` → `Disable`) или сделайте его администратором.
3. Один раз авторизуйте ngrok:

```powershell
ngrok config add-authtoken <TOKEN>
```

4. Запустите туннель:

```powershell
ngrok http 5081
```

5. Скопируйте адрес из лога в `.env`:

```text
WEBHOOK_BASE_URL=https://your-name.ngrok-free.dev
```

Домен постоянный и у каждого разработчика свой. Инспектор запросов — <http://127.0.0.1:4040>, работает только при запущенном ngrok.

## Запуск RabbitMQ

```bash
docker compose up -d rabbitmq postgres database-init
```

UI: <http://localhost:15672>, логин/пароль `teammemory` / `teammemory`.

## Сборка

```bash
dotnet build TeamMemory.sln
```

## Запуск сервисов

**Терминал 1 — Analytics** (запускать первым, чтобы очереди существовали до публикации):

```bash
dotnet run --project src/AnalyticsService/AnalyticsService.csproj
```

**Терминал 2 — Ingestion**:

```bash
dotnet run --project src/IngestionService/IngestionService.csproj
```

Ingestion слушает `http://localhost:5081` и в Development сам вызывает `SetWebhook`. Ожидаемый лог:

```text
Telegram webhook set: https://your-name.ngrok-free.dev/api/telegram/webhook
```

**Dashboard** (порт `5080`, frontend — `5173`):

```bash
dotnet run --project src/DashboardService/DashboardService.csproj
```

## Проверка

```powershell
$token = ((Get-Content .env | Where-Object { $_ -match '^\s*TELEGRAM_BOT_TOKEN\s*=' }) -split '=',2)[1].Trim()
Invoke-RestMethod "https://api.telegram.org/bot$token/getWebhookInfo" |
    Select-Object -ExpandProperty result |
    Format-List url, pending_update_count, last_error_message
```

Должно быть: `url` — твой ngrok-адрес, `pending_update_count: 0`, `last_error_message` пустой.

Отправьте сообщение в группу. В логах:

```text
[Ingestion] Received Telegram webhook. ChatId: ..., MessageId: .... Publishing event with EventId: <GUID>
[Analytics] Successfully received event from RabbitMQ! EventId: <GUID>, ChatId: ..., Sender: ...
```

`EventId` должен совпадать.

> Без Telegram можно использовать `POST /api/dev/telegram/messages` (только Development).

## События и RabbitMQ

MassTransit создаёт exchange `Shared.Contracts.Telegram:TelegramMessageCreated` (fanout) и очередь консьюмера.
Публикация идёт в exchange, routing key не используется.

Контракты меняются только в `shared/Shared.Contracts`. Переименование типа или namespace ломает топологию RabbitMQ.
Новые поля добавляются как nullable, ломающие изменения — через новую версию namespace.

Analytics подписан на все восемь конкретных событий из `Shared.Contracts`; подробности и ограничения описаны в [документации Analytics](src/AnalyticsService/README.md).

## Ограничения

- GitHub webhooks не реализованы.
- Для AI-анализа нужно настроить доступный LLM API и модель; команда определяется автоматически по ID чата.
- HMAC для GitHub не реализован.
- Inbox/outbox Analytics хранятся в PostgreSQL. Редактирования Telegram и Git пока только логируются; CI отсутствует.
- Регистрация групп в Dashboard через `groups.updated.v1` пока внешняя; для локального демо
  группа создаётся из `DEMO_GROUP_CHAT_ID`.

## Лицензии

- .NET — MIT.
- RabbitMQ — MPL 2.0.
- MassTransit **8.5.12** — Apache-2.0. Версия 9.x коммерческая, не обновлять без согласования.
