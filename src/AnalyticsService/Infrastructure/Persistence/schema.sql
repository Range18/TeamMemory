-- Initial schema for the main Telegram scenario. Applied idempotently on startup.
CREATE TABLE IF NOT EXISTS analysis_batches (
    id uuid PRIMARY KEY,
    team_id text NOT NULL,
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
    team_id text NOT NULL,
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
    team_id text NOT NULL,
    employee_id bigint NOT NULL,
    version integer NOT NULL,
    state jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_tasks_team_employee ON tasks(team_id, employee_id);
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
