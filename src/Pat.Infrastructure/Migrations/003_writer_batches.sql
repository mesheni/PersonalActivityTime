-- Receipt and event insertion commit together; replay after a crash is idempotent.
CREATE TABLE committed_batches (id TEXT PRIMARY KEY, committed_utc INTEGER NOT NULL);
