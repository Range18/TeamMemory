# Analytics Service

Актуально на 23 ноября 2026 года. Документ описывает текущие ORM-модели и контракты в репозитории; схема развёрнутой БД отдельно не проверялась.

Analytics принимает события Telegram, сохраняет их в PostgreSQL, анализирует группы сообщений локальной LLM и сохраняет изменения задач. Dashboard получает события новых задач, блокеров и риска через RabbitMQ.

- [Схема базы данных](database-schema.md)
- [Контракт Analytics → Dashboard](dashboard-contract.md)
- [Запуск](#запуск)
- [Подробная архитектура](../../docs/analytics-architecture.md)
- [Функциональные требования](../../docs/analytics-functional-requirements.md)

## Запуск

Нужны .NET 10 SDK, PostgreSQL, RabbitMQ и локальный файл GGUF-модели. Из корня репозитория:

```powershell
docker compose up -d postgres rabbitmq
dotnet build TeamMemory.sln
dotnet run --project src/AnalyticsService/AnalyticsService.csproj
```

До отправки сообщений задайте `Analytics:ChatTeams` и `Llama:ModelPath` в [appsettings.json](appsettings.json) или конфигурации окружения. По умолчанию привязка чатов пустая, backend модели — CPU. Таблицы пустой БД создаются при старте; веса модели загружаются при первом анализе.

Пример конфигурации с абсолютным путём к модели:

```json
{
  "Analytics": {
    "ChatTeams": {
      "-1001234567890": "team-backend"
    }
  },
  "Llama": {
    "ModelPath": "D:/Models/qwen2.5-7b-instruct-q5_k_m.gguf"
  }
}
```

Полный анализ реализован для `TelegramMessageCreated`. Ещё семь подписок — `TelegramMessageEdited`, `GitPushCommitted` и пять событий pull request — только логируют приём. Для реального Telegram также нужен запущенный Ingestion Service. Проверки хранения и правил применения изменений описаны в [README проверок](../../tests/AnalyticsService.Checks/README.md).
