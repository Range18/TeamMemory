-- Additive upgrade for databases previously initialized with EnsureCreated.
CREATE TABLE IF NOT EXISTS analytics_events (
    event_id uuid PRIMARY KEY,
    chat_id bigint,
    task_id uuid NOT NULL,
    employee_id bigint NOT NULL,
    kind text NOT NULL,
    title text NOT NULL,
    description text NOT NULL,
    evidence_event_ids uuid[] NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    received_at_utc timestamptz NOT NULL
);
ALTER TABLE analytics_events DROP COLUMN IF EXISTS team_id;
CREATE INDEX IF NOT EXISTS ix_analytics_events_chat_date ON analytics_events(chat_id, occurred_at_utc);
CREATE TABLE IF NOT EXISTS digest_notifications (
    event_id uuid PRIMARY KEY REFERENCES analytics_events(event_id) ON DELETE CASCADE,
    chat_id bigint NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL,
    sent_at_utc timestamptz
);
CREATE INDEX IF NOT EXISTS ix_digest_notifications_pending ON digest_notifications(created_at_utc)
    WHERE sent_at_utc IS NULL;
