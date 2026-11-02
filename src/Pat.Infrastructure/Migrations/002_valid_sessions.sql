-- sessions is a derived cache; discard corrupt rows from the old lookback clipping.
DELETE FROM sessions WHERE ended_utc <= started_utc;
CREATE TRIGGER sessions_valid_insert BEFORE INSERT ON sessions
WHEN NEW.ended_utc <= NEW.started_utc
BEGIN SELECT RAISE(ABORT, 'session end must follow start'); END;
CREATE TRIGGER sessions_valid_update BEFORE UPDATE ON sessions
WHEN NEW.ended_utc <= NEW.started_utc
BEGIN SELECT RAISE(ABORT, 'session end must follow start'); END;
CREATE INDEX ix_raw_events_kind_ts ON raw_events(kind, ts_utc);
