-- Initial schema for the main Telegram scenario. Applied idempotently on startup.
CREATE TABLE IF NOT EXISTS analysis_batches (
    id uuid PRIMARY KEY,
    source_instance text NOT NULL,
    chat_id bigint NOT NULL,
    thread_id bigint NOT NULL,
    status text NOT NULL DEFAULT 'ready',
    created_at timestamptz NOT NULL DEFAULT now(),
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    input jsonb,
    result jsonb,
    error text
);
CREATE TABLE IF NOT EXISTS inbox_events (
    event_id uuid PRIMARY KEY,
    source_instance text NOT NULL,
    chat_id bigint NOT NULL,
    message_id bigint NOT NULL,
    thread_id bigint NOT NULL,
    employee_id bigint NOT NULL,
    author text NOT NULL,
    text text NOT NULL,
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    correlation_id text NOT NULL,
    payload jsonb NOT NULL,
    batch_id uuid REFERENCES analysis_batches(id),
    UNIQUE(source_instance, chat_id, message_id)
);
CREATE INDEX IF NOT EXISTS ix_inbox_pending ON inbox_events(received_at) WHERE batch_id IS NULL;
CREATE INDEX IF NOT EXISTS ix_inbox_batch ON inbox_events(batch_id);
CREATE INDEX IF NOT EXISTS ix_batches_ready ON analysis_batches(next_attempt_at) WHERE status = 'ready';
CREATE TABLE IF NOT EXISTS tasks (
    id uuid PRIMARY KEY,
    chat_id bigint,
    employee_id bigint NOT NULL,
    version integer NOT NULL,
    state jsonb NOT NULL
);
-- Old databases did not have a separate task chat column.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS chat_id bigint;
CREATE INDEX IF NOT EXISTS ix_tasks_chat_employee ON tasks(chat_id, employee_id);
CREATE TABLE IF NOT EXISTS task_changes (
    id uuid PRIMARY KEY,
    task_id uuid NOT NULL REFERENCES tasks(id),
    batch_id uuid REFERENCES analysis_batches(id),
    change_ref text NOT NULL,
    action text NOT NULL,
    payload jsonb NOT NULL,
    effective_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(batch_id, change_ref)
);
CREATE TABLE IF NOT EXISTS outbox_messages (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox_messages(created_at) WHERE published_at IS NULL;

CREATE TABLE IF NOT EXISTS analytics_schema_migrations (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

-- Upgrade the previous team-scoped schema without deleting messages, tasks or history.
DO $migration$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM analytics_schema_migrations WHERE version = 1) THEN
        UPDATE tasks SET chat_id = NULLIF((state->>'chat_id')::bigint, 0) WHERE chat_id IS NULL;

        -- A legacy task without chat metadata is assigned only when its history is unambiguous.
        UPDATE tasks AS task SET chat_id = history.chat_id
        FROM (
            SELECT change.task_id, min(batch.chat_id) AS chat_id
            FROM task_changes AS change
            JOIN analysis_batches AS batch ON batch.id = change.batch_id
            GROUP BY change.task_id HAVING count(DISTINCT batch.chat_id) = 1
        ) AS history
        WHERE task.id = history.task_id AND task.chat_id IS NULL;

        UPDATE tasks SET state = (state - 'team_id') || jsonb_build_object('chat_id', coalesce(chat_id, 0));

        -- Pending snapshots may have candidates from other chats of the old team.
        -- Keep only this chat's candidates; an unsafe saved result will fail normal validation.
        UPDATE analysis_batches AS batch SET input = (input - 'team_id') || jsonb_build_object(
            'chat_id', batch.chat_id,
            'tasks', coalesce((
                SELECT jsonb_agg((candidate.value - 'team_id') || jsonb_build_object('chat_id', task.chat_id))
                FROM jsonb_array_elements(coalesce(batch.input->'tasks', '[]'::jsonb)) AS candidate(value)
                JOIN tasks AS task ON task.id = (candidate.value->>'id')::uuid
                WHERE task.chat_id = batch.chat_id
            ), '[]'::jsonb))
        WHERE input IS NOT NULL AND status <> 'applied';

        UPDATE outbox_messages AS message SET payload = (payload - 'TeamId') || jsonb_build_object(
            'ChatId', coalesce((payload->>'ChatId')::bigint,
                (SELECT chat_id FROM tasks WHERE id = (message.payload->>'TaskId')::uuid)))
        WHERE published_at IS NULL;

        ALTER TABLE analysis_batches DROP COLUMN IF EXISTS team_id;
        ALTER TABLE inbox_events DROP COLUMN IF EXISTS team_id;
        ALTER TABLE tasks DROP COLUMN IF EXISTS team_id;
        INSERT INTO analytics_schema_migrations(version) VALUES(1);
    END IF;
END
$migration$;
