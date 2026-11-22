# Analytics Service

Актуально на 23 ноября 2026 года. Документ описывает текущие ORM-модели и контракты в репозитории; схема развёрнутой БД отдельно не проверялась.

Analytics принимает события Telegram, сохраняет их в PostgreSQL, анализирует группы сообщений через OpenAI-совместимый LLM API и сохраняет изменения задач. Dashboard получает события новых задач, блокеров и риска через RabbitMQ.

- [Схема базы данных](database-schema.md)
- [Контракт Analytics → Dashboard](dashboard-contract.md)
- [Запуск](#запуск)
- [Подробная архитектура](../../docs/analytics-architecture.md)
- [Функциональные требования](../../docs/analytics-functional-requirements.md)

## Запуск

Нужны .NET 10 SDK, PostgreSQL, RabbitMQ и доступный OpenAI-совместимый endpoint `POST /v1/chat/completions`. По умолчанию указан локальный Ollama API на `http://localhost:11434/v1/chat/completions` с моделью `qwen2.5:7b`. Сервер модели запускается отдельно; Docker Compose проекта поднимает только БД и брокер.

Из корня репозитория:

```powershell
docker compose up -d postgres rabbitmq
dotnet build TeamMemory.sln
dotnet run --project src/AnalyticsService/AnalyticsService.csproj
```

До отправки сообщений задайте `Analytics:ChatTeams`, `LlmApi:Endpoint` и `LlmApi:Model` в [appsettings.json](appsettings.json) или конфигурации окружения. По умолчанию привязка чатов пустая. Таблицы пустой БД создаются при старте. Файл модели и LLamaSharp в процессе Analytics больше не используются.

Пример конфигурации:

```json
{
  "Analytics": {
    "ChatTeams": {
      "-1001234567890": "team-backend"
    }
  },
  "LlmApi": {
    "Endpoint": "http://localhost:11434/v1/chat/completions",
    "Model": "qwen2.5:7b",
    "MaxTokens": 1500,
    "TimeoutSeconds": 300,
    "UseJsonMode": true
  }
}
```

Если сервер требует ключ, передайте его через переменную окружения или .NET User Secrets; в репозиторий ключ не записывается:

```powershell
$env:LlmApi__ApiKey = "your-api-key"
$env:LlmApi__Endpoint = "https://your-llm-server.example/v1/chat/completions"
$env:LlmApi__Model = "your-model-id"
dotnet run --project src/AnalyticsService/AnalyticsService.csproj
```

### Формат вызова LLM

[HttpMessageBatchAnalyzer](Analysis/HttpMessageBatchAnalyzer.cs) отправляет `model`, два сообщения `messages` с ролями `system` и `user`, `max_tokens`, `temperature = 0.1`, `stream = false` и по умолчанию `response_format: { "type": "json_object" }`. Инструкции вынесены в [AnalysisPrompt](Analysis/AnalysisPrompt.cs); в user-сообщении передаётся JSON снимка `AnalysisInput`. API-ключ, если задан, отправляется в заголовке `Authorization: Bearer ...`.

Ожидается ответ Chat Completions с JSON анализа в `choices[0].message.content`: `schema_version`, `changes`, `ambiguities`. `finish_reason` должен отсутствовать/null либо равняться `stop`; обрезанные ответы и пустое содержимое переводят группу в `needs_review`. После ответа сохраняются прежние проверки изменений задач.

Сервер должен поддерживать перечисленные параметры и выбранную модель. Если он не поддерживает `response_format`, задайте `LlmApi:UseJsonMode = false`: инструкция вернуть JSON и проверка ответа останутся. Пример локального совместимого API приведён в [документации Ollama](https://docs.ollama.com/api/openai-compatibility). Это интеграция Chat Completions, не Responses API.

Сетевые ошибки, неуспешные HTTP-статусы и тайм-аут обрабатываются существующими повторными попытками группы. Локального подсчёта токенов больше нет: ограничения контекста проверяет LLM-сервер. Сервис не обрезает сообщения автоматически. Реальный endpoint и ключ нужны для проверки с живой моделью; автоматические проверки используют подменный HTTP-обработчик.

Полный анализ реализован для `TelegramMessageCreated`. Ещё семь подписок — `TelegramMessageEdited`, `GitPushCommitted` и пять событий pull request — только логируют приём. Для реального Telegram также нужен запущенный Ingestion Service. Проверки хранения и правил применения изменений описаны в [README проверок](../../tests/AnalyticsService.Checks/README.md).
