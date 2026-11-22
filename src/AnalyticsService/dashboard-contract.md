# Контракт Analytics → Dashboard

Актуально на 23 ноября 2026 года. Описание основано на текущем коде репозитория.

[Вернуться к README сервиса](README.md).

## Транспорт и направление обмена

Текущий API — асинхронные интеграционные события через RabbitMQ/MassTransit. Analytics публикует, Dashboard подписывается. HTTP endpoints для запросов Dashboard к Analytics, обратные команды и ответы сейчас отсутствуют. Dashboard не обращается к таблицам Analytics в текущем коде.

Контракты определены в [Shared.Contracts.Analytics](../../shared/Shared.Contracts/Analytics/AnalyticsEvents.cs), общие поля — в [IntegrationEventBase](../../shared/Shared.Contracts/IntegrationEventBase.cs).

| Тип события | Когда публикуется | `CorrelationId` | `EvidenceEventIds` |
| --- | --- | --- | --- |
| `NewTaskReported` | Принято создание новой задачи | UUID группы анализа строкой | Непустой список UUID сообщений, подтверждающих создание |
| `BlockerDetected` | К задаче добавлен новый блокер | UUID группы анализа строкой | Непустой список UUID сообщений, подтверждающих блокер |
| `TaskAtRisk` | Признак риска задачи меняется с `false` на `true` | UUID задачи строкой | Пустой массив: риск рассчитан кодом |

Пространство имён всех трёх типов — `Shared.Contracts.Analytics`. События наследуют `TaskAnalyticsEvent`, который наследует `IntegrationEventBase`. Все три события имеют одинаковый набор полей.

## Поля сообщения

| Поле C# | JSON в транспорте | Тип | Семантика |
| --- | --- | --- | --- |
| `EventId` | `eventId` | UUID | Идентификатор события; сохраняется при повторной публикации |
| `OccurredAt` | `occurredAt` | ISO 8601 date-time | Время создания аналитического события в UTC, а не время исходного сообщения или публикации |
| `Source` | `source` | string | Всегда `analytics` для этих событий |
| `CorrelationId` | `correlationId` | string | Идентификатор группы либо задачи по таблице типов выше |
| `TeamId` | `teamId` | string | Команда задачи |
| `TaskId` | `taskId` | UUID | Идентификатор задачи Analytics |
| `Title` | `title` | string | Заголовок задачи на момент формирования события |
| `EmployeeId` | `employeeId` | int64 | Идентификатор сотрудника, полученный из Telegram `SenderId` |
| `Description` | `description` | string | Для новой задачи — заголовок; для блокера — текст блокера; для риска — описание превышения оценки |
| `EvidenceEventIds` | `evidenceEventIds` | UUID[] | Идентификаторы исходных событий Telegram, а не Telegram `MessageId` |

Все поля должны присутствовать; значений `null` контракт не предусматривает. Для `TaskAtRisk` допустим и используется пустой `evidenceEventIds`. C# `required` задаёт обязательность при создании объекта; отдельного валидатора входящих аналитических событий в Dashboard сейчас нет.

## JSON и маршрутизация

В RabbitMQ используется JSON-envelope MassTransit с `Content-Type: application/vnd.masstransit+json`. Payload находится внутри `message`; имена полей в транспорте — `camelCase`. Это стандартные настройки [сериализатора MassTransit 8.5.12](https://github.com/MassTransit/MassTransit/blob/v8.5.12/src/MassTransit/Serialization/SystemTextJsonMessageSerializer.cs); сервисы их не переопределяют.

Ниже приведены только payload-объекты `message`, без служебного envelope. Они отличаются от PascalCase JSON, который хранится в `outbox_messages.payload` до публикации.

Публикация выполняется по конкретному типу:

```csharp
await bus.Publish(message, type, context => context.MessageId = item.EventId, ct);
```

Имена очередей и привязки создаёт `ConfigureEndpoints`; сервис не задаёт их вручную как часть контракта. Dashboard регистрирует `AnalyticsEventConsumer<NewTaskReported>`, `AnalyticsEventConsumer<BlockerDetected>` и `AnalyticsEventConsumer<TaskAtRisk>`. Для подключения другого подписчика предпочтительно использовать те же типы из `Shared.Contracts` и MassTransit; приведённый payload сам по себе не является полным транспортным сообщением.

## Примеры payload

`NewTaskReported`:

```json
{
  "eventId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "occurredAt": "2026-11-23T09:00:00+00:00",
  "source": "analytics",
  "correlationId": "22222222-2222-4222-8222-222222222222",
  "teamId": "team-backend",
  "taskId": "11111111-1111-4111-8111-111111111111",
  "title": "Интеграция оплаты",
  "employeeId": 123456789,
  "description": "Интеграция оплаты",
  "evidenceEventIds": ["33333333-3333-4333-8333-333333333333"]
}
```

`BlockerDetected`:

```json
{
  "eventId": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
  "occurredAt": "2026-11-23T09:10:00+00:00",
  "source": "analytics",
  "correlationId": "44444444-4444-4444-8444-444444444444",
  "teamId": "team-backend",
  "taskId": "11111111-1111-4111-8111-111111111111",
  "title": "Интеграция оплаты",
  "employeeId": 123456789,
  "description": "Нет доступа к API",
  "evidenceEventIds": ["55555555-5555-4555-8555-555555555555"]
}
```

`TaskAtRisk`:

```json
{
  "eventId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "occurredAt": "2026-11-23T09:20:00+00:00",
  "source": "analytics",
  "correlationId": "11111111-1111-4111-8111-111111111111",
  "teamId": "team-backend",
  "taskId": "11111111-1111-4111-8111-111111111111",
  "title": "Интеграция оплаты",
  "employeeId": 123456789,
  "description": "Calendar duration exceeds estimate × 1.5.",
  "evidenceEventIds": []
}
```

## Доставка и обработка

1. Analytics сохраняет событие в outbox в транзакции изменения задачи. `event_type` содержит имя C#-типа, например `NewTaskReported`.
2. [OutboxPublisher](Processing/OutboxPublisher.cs) получает блокировку издателя и выбирает до 100 событий с `published_at = NULL`, сортируя по `created_at`, затем `event_id`.
3. Событие публикуется через MassTransit; транспортный `MessageId` равен payload `EventId`.
4. После успешного вызова публикации Analytics выставляет `published_at`. Эта отметка не подтверждает бизнес-обработку события Dashboard.
5. После прохода издатель ждёт 2 секунды. При ошибке событие остаётся доступным для следующей попытки.

Семантика доставки допускает повторы. При сбое после отправки, но до сохранения `published_at`, событие будет опубликовано повторно с тем же `EventId`. Подписчик, сохраняющий бизнес-результат, должен дедуплицировать его по `EventId` и атомарно фиксировать факт обработки со своими изменениями. Такой механизм в текущем Dashboard ещё не реализован.

Контракт не обещает глобального порядка обработки событий подписчиком. Полной версии состояния задачи и `Task.Version` в событии нет. При доработке Dashboard нельзя считать этот поток полноценной репликацией таблицы `tasks`.

Текущий [AnalyticsEventConsumer](../DashboardService/Consumers/AnalyticsEventConsumer.cs) только логирует тип, событие, команду и задачу. Dashboard настраивает три повторные попытки обработки через 1, 5 и 15 секунд; хранения событий, UI и дайджестов пока нет.

## Ограничения и развитие контракта

- Отдельные события прогресса, изменения оценки, следующего шага, завершения задачи, снятия блокера и снятия риска не публикуются.
- `TaskAtRisk` формируется при незавершённой задаче с оценкой, когда календарная длительность с начала строго превышает `EstimateDays × RiskMultiplier`; по умолчанию множитель равен `1.5`.
- `NewTaskReported` не содержит итоговый статус или оценку, даже если они изменились в той же группе анализа.
- Поля `schema_version` из ответа LLM нет в интеграционных событиях. Это внутренний формат анализа, не версия API для Dashboard.
- Контракты меняются в `shared/Shared.Contracts`. В текущем соглашении проекта новые поля добавляются как nullable; несовместимые изменения вводятся через новую версию namespace. Переименование существующего типа/namespace меняет топологию MassTransit и требует согласованного обновления подписчиков.
