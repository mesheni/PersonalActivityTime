-- Схема журнала активности и материализованных сессий.

CREATE TABLE applications (
    id              INTEGER PRIMARY KEY,
    exe_path        TEXT NOT NULL UNIQUE,
    exe_name        TEXT NOT NULL,
    first_seen_utc  INTEGER NOT NULL
);

CREATE TABLE raw_events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc          INTEGER NOT NULL,
    kind            INTEGER NOT NULL,
    app_id          INTEGER NULL REFERENCES applications(id),
    window_title    TEXT NULL,
    payload         TEXT NULL
);

CREATE INDEX ix_raw_events_ts ON raw_events(ts_utc);

CREATE TABLE sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc     INTEGER NOT NULL,
    ended_utc       INTEGER NOT NULL,
    app_id          INTEGER NULL REFERENCES applications(id),
    is_idle         INTEGER NOT NULL DEFAULT 0,
    window_title    TEXT NULL,
    project         TEXT NULL,
    category_name   TEXT NOT NULL DEFAULT 'Другое'
);

CREATE INDEX ix_sessions_started ON sessions(started_utc);
