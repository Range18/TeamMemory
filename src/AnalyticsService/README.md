# Основной сценарий Analytics

`TelegramMessageCreated` → MassTransit/RabbitMQ → PostgreSQL inbox → группа сообщений →
локальная GGUF-модель через LLamaSharp → проверка JSON → задачи и история в PostgreSQL →
outbox → RabbitMQ → подписки DashboardService.

Модель загружается один раз, новый контекст создаётся на каждую группу. Анализ выполняется
в фоне, вне consumer и вне транзакции БД. Ответ сохраняется до применения и используется
повторно после перезапуска. PostgreSQL хранит события, снимок запроса, результат, текущие
задачи, историю операций и исходящие события. Уникальные ключи защищают от повторного
приёма EventId и повторного webhook с другим EventId.

## Запуск

Нужны .NET 10, Docker и самостоятельно скачанный `qwen2.5-7b-instruct-q5_k_m.gguf`.
Файл модели не включён в репозиторий. Промпт использует ChatML для Qwen2.5 Instruct.

```powershell
docker compose up -d postgres rabbitmq
dotnet user-secrets set "Analytics:ChatTeams:-1001234567890" "team-1" --project src/AnalyticsService
dotnet user-secrets set "Llama:ModelPath" "E:\models\qwen2.5-7b-instruct-q5_k_m.gguf" --project src/AnalyticsService
dotnet run --project src/DashboardService
# В другом терминале:
dotnet run --project src/AnalyticsService
```

Замените ChatId на свой. Сначала запустите Analytics и Dashboard, чтобы MassTransit создал
их очереди, затем Ingestion. Для user-secrets нужна среда Development (в launchSettings она
уже установлена). В другой среде используйте конфигурацию или переменные окружения.
Схема БД создаётся при старте; скрипт находится в `Infrastructure/Persistence/schema.sql`.

Строка подключения — `ConnectionStrings:Analytics`. Настройки групп: `Analytics:IdleSeconds`
(60), `MaxWaitSeconds` (300), `MaxMessages` (100), `PollSeconds` (5).
`Analytics:ChatTeams` связывает ChatId с командой. Неизвестные чаты отправляются в error queue.
В основном сценарии стабильный Telegram SenderId служит идентификатором сотрудника;
полный справочник сотрудников и внешних идентичностей не реализован.

По умолчанию подключён CPU backend, `GpuLayerCount=0`. Для NVIDIA с установленным CUDA 12:

```powershell
dotnet user-secrets set "Llama:GpuLayerCount" "20" --project src/AnalyticsService
dotnet run --project src/AnalyticsService -p:LlamaBackend=Cuda12
```

CPU и CUDA backend должны соответствовать версии LLamaSharp (0.26.0).

## Вход и результат

Ingestion передаёт `SenderId`, `ThreadId` и исходное время сообщения. Текстовые сообщения
одной команды, source instance, чата и темы группируются до минуты тишины, пяти минут
ожидания либо 100 сообщений. Без Telegram topic используется общая группа чата.
ReplyToMessageId сохраняется в payload; восстановление произвольных цепочек reply пока не реализовано.

В контекст попадают новые сообщения и задачи авторов из той же команды, максимум 20
кандидатов. Больше кандидатов или переполнение контекста модели требуют `needs_review`;
сервис не обрезает текст молча. Предыдущая переписка пока не подмешивается.

LLM возвращает `schema_version`, `changes`, `ambiguities`. Операции: `create_task`,
`record_progress`, `add_blocker`, `resolve_blocker`, `set_next_step`, `set_estimate`, `set_status`.
Каждое изменение содержит ссылки `evidence_event_ids` и confidence ≥ 0.85.
Для новой задачи используется `new_task_ref`; существующий `task_id` берётся из контекста.
Неизвестные ссылки/поля, неоднозначность и невалидный JSON не меняют задачи.
Завершение подзадачи записывается как прогресс; открытый блокер переводит задачу в `blocked`.

Отдельная проверка перед каждой итерацией анализа выставляет риск при календарной
длительности `> EstimateDays × 1.5`. Новое сообщение для этого не требуется. Для проверки
примера из архитектуры сначала сообщите модели о новой задаче и оценке в днях, затем
отправьте сообщения о прогрессе, блокере и следующем шаге.

Исходящие контракты в `Shared.Contracts.Analytics`:

- `NewTaskReported` — создана новая задача из сообщения.
- `BlockerDetected` — добавлен новый открытый блокер.
- `TaskAtRisk` — задача впервые перешла в риск (или вернулась в риск после его снятия).

События содержат EventId, TeamId, TaskId, EmployeeId, заголовок, описание и доказательства.
У риска доказательства пусты: это вычисление по сохранённым срокам, а не новый вывод LLM.
Dashboard подписан на все три типа и логирует приём. Хранение и интерфейс Dashboard,
дайджесты и HTTP API не входят в этот сценарий. Outbox доставляет события как минимум
один раз; при добавлении побочных действий в Dashboard нужна дедупликация по EventId.

Для первой версии PostgreSQL advisory lock допускает одного активного analysis worker
и одного publisher на базу. Приём событий работает независимо от них. Проверка риска
может задержаться до завершения текущего LLM-вызова (таймаут по умолчанию 300 секунд).
Временные ошибки анализа повторяются до пяти раз с задержкой. `failed` и `needs_review`
сохраняются для диагностики; автоматический разбор таких групп не входит в сценарий.
Git и редактирования Telegram остаются логирующими подписками без анализа.

## Проверки

```powershell
dotnet build TeamMemory.sln
dotnet run --project tests/AnalyticsService.Checks
docker compose -p teammemory-analytics-checks -f tests/AnalyticsService.Checks/docker-compose.yml up -d --wait
dotnet run --project tests/AnalyticsService.Checks -- --integration
docker compose -p teammemory-analytics-checks -f tests/AnalyticsService.Checks/docker-compose.yml down
```

Локальные проверки проверяют операции модели, доказательства, порог риска и запреты
на частичное применение. Интеграционный режим использует отдельные PostgreSQL/RabbitMQ
на 5433/5673: входной consumer, дедупликацию, применение, outbox и доставку подписчику.
Ответ модели в этих проверках задан явно; настоящий inference требует GGUF-файл.
