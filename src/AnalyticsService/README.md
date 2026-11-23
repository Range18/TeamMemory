# Analytics Service

Актуально на 23 ноября 2026 года. Документ описывает текущие ORM-модели и контракты в репозитории; схема развёрнутой БД отдельно не проверялась.

Analytics принимает события Telegram, сохраняет их в PostgreSQL, анализирует группы сообщений через OpenAI-совместимый LLM API и сохраняет изменения задач. Dashboard получает события новых задач, блокеров и риска через RabbitMQ.

- [Схема базы данных](database-schema.md)
- [Контракт Analytics → Dashboard](dashboard-contract.md)
- [Запуск](#запуск)
- [Подробная архитектура](../../docs/analytics-architecture.md)
- [Функциональные требования](../../docs/analytics-functional-requirements.md)

## Запуск

Нужны .NET 10 SDK, PostgreSQL, RabbitMQ и доступный OpenAI-совместимый Chat Completions API. Рабочие настройки хранятся в корневом `.env`; [шаблон](../../.env.example) настроен на DeepSeek. В `appsettings.json` остаётся логирование. Для локальной модели запускайте LLM-сервер отдельно: Docker Compose проекта поднимает только БД и брокер.

При первом запуске создайте `.env`, если его ещё нет:

```powershell
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
```

Заполните ключ API:

```dotenv
LlmApi__Endpoint=https://api.deepseek.com/chat/completions
LlmApi__Model=deepseek-flash
LlmApi__ApiKey=your-api-key
LlmApi__MaxTokens=1500
LlmApi__TimeoutSeconds=300
LlmApi__UseJsonMode=true
```

Двойное подчёркивание `__` разделяет вложенные настройки .NET: `LlmApi__ApiKey` соответствует `LlmApi:ApiKey`. Вручную задавать команду не требуется: по умолчанию `TeamId` равен строковому ID Telegram-чата, например `-5551321053`. Каждый чат получает отдельную команду. Необязательное переопределение: `Analytics__ChatTeams__-5551321053=custom-team`; оно имеет приоритет над автоматическим значением. `ChatTeams` больше не служит списком разрешённых чатов: валидные сообщения принимаются и без записи в нём.

Полный набор параметров БД, RabbitMQ и обработки указан в `.env.example`. Настройки подключения должны соответствовать фактическим учётным данным PostgreSQL и RabbitMQ; изменение `.env` не меняет пользователей уже работающих контейнеров. Изменение привязки не переносит ранее сохранённые задачи и сообщения между командами.

Analytics загружает ближайший `.env`, поднимаясь от каталога приложения вверх, до создания Host. Переменные окружения текущего процесса имеют приоритет и не перезаписываются значениями из файла. После изменения `.env` перезапустите сервис. При развёртывании без `.env` те же настройки можно передавать через переменные окружения.

Из корня репозитория:

```powershell
docker compose up -d postgres rabbitmq
dotnet build TeamMemory.sln
dotnet run --project src/AnalyticsService/AnalyticsService.csproj
```

`.env` исключён из Git, ключ в `.env.example` пустой. Таблицы пустой БД создаются при старте. Файл модели и LLamaSharp в процессе Analytics не используются.

### Формат вызова LLM

[HttpMessageBatchAnalyzer](Analysis/HttpMessageBatchAnalyzer.cs) отправляет `model`, два сообщения `messages` с ролями `system` и `user`, `max_tokens`, `temperature = 0.1`, `stream = false` и по умолчанию `response_format: { "type": "json_object" }`. Инструкции вынесены в [AnalysisPrompt](Analysis/AnalysisPrompt.cs); в user-сообщении передаётся JSON снимка `AnalysisInput`. API-ключ, если задан, отправляется в заголовке `Authorization: Bearer ...`.

Ожидается ответ Chat Completions с JSON анализа в `choices[0].message.content`: `schema_version`, `changes`, `ambiguities`. `finish_reason` должен отсутствовать/null либо равняться `stop`; обрезанные ответы и пустое содержимое переводят группу в `needs_review`. После ответа сохраняются прежние проверки изменений задач.

Сервер должен поддерживать перечисленные параметры и выбранную модель. Если он не поддерживает `response_format`, задайте `LlmApi:UseJsonMode = false`: инструкция вернуть JSON и проверка ответа останутся. Пример локального совместимого API приведён в [документации Ollama](https://docs.ollama.com/api/openai-compatibility). Это интеграция Chat Completions, не Responses API.

Сетевые ошибки, неуспешные HTTP-статусы и тайм-аут обрабатываются существующими повторными попытками группы. Локального подсчёта токенов больше нет: ограничения контекста проверяет LLM-сервер. Сервис не обрезает сообщения автоматически. Реальный endpoint и ключ нужны для проверки с живой моделью; автоматические проверки используют подменный HTTP-обработчик.

Полный анализ реализован для `TelegramMessageCreated`. Ещё семь подписок — `TelegramMessageEdited`, `GitPushCommitted` и пять событий pull request — только логируют приём. Для реального Telegram также нужен запущенный Ingestion Service. Проверки хранения и правил применения изменений описаны в [README проверок](../../tests/AnalyticsService.Checks/README.md).
