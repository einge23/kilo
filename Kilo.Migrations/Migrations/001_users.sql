CREATE TABLE users (
    id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clerk_user_id   text NOT NULL UNIQUE,
    time_zone       text NOT NULL DEFAULT 'UTC',
    measurement_system text NOT NULL DEFAULT 'imperial'
                       CHECK (measurement_system IN ('imperial', 'metric')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (btrim(clerk_user_id) <> '')
);
