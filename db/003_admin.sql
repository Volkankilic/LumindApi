-- =====================================================================================
-- 003_admin.sql : admin panel (phase 7): admin accounts, their sessions, recovery codes, crisis acknowledgements
-- =====================================================================================
-- Purpose : adds the tables of the admin panel and the indexes it needs. Plain PostgreSQL: no extensions, no DO $$
--           blocks (runs in psql AND in DBeaver), the application generates every id.
--             admin_users            admin accounts. Only a PBKDF2 hash of the password is kept (never the password itself).
--                                    The TOTP secret is ENCRYPTED by the application before it is stored (no key is needed to run this script).
--             admin_refresh_tokens   admin sessions: only the SHA-256 of the token.
--             admin_recovery_codes   single use recovery codes: only a keyed hash.
--             crisis_acknowledgements "a team member took care of this crisis flag": message, admin, time. NO free text column.
--           Indexes: unique participant label, flagged messages by time, audit rows by actor/action/time (login counts).
-- Where   : the single Railway PostgreSQL database. Run it AFTER 001 and 002 and their verify.sql. Synthetic data only.
-- Safe to run again: IDEMPOTENT (IF NOT EXISTS) and a SINGLE TRANSACTION (all or nothing).
-- BEFORE YOU RUN (one read-only check): the unique label index fails if two participants share a label. This returns no row
--           when all is well:
--             SELECT label, count(*) FROM public.participants WHERE label IS NOT NULL AND label <> '' GROUP BY label HAVING count(*) > 1;
--           If it returns rows the script stops with an error and changes nothing; fix the labels by hand first.
-- Order   : run this file, then verify.sql (it now expects schema version 3). Then create the admins (db/README.md).
-- Rollback: rollback/003_admin_down.sql (DESTRUCTIVE for the admin tables: it drops them).
-- Rule    : never edit this file after it ran successfully. A change is a new numbered file.
-- Note    : the column "timestamp" is quoted because it is also a type name.
-- =====================================================================================

BEGIN;

-- Admin accounts. Created only from the command line (create-admin), never through the panel.
CREATE TABLE IF NOT EXISTS public.admin_users (
    id              uuid        NOT NULL,
    username        text        NOT NULL,
    password_hash   text        NOT NULL,
    totp_secret     text        NULL,
    totp_enabled    boolean     NOT NULL DEFAULT FALSE,
    totp_last_step  bigint      NOT NULL DEFAULT 0,
    status          text        NOT NULL,
    failed_attempts integer     NOT NULL DEFAULT 0,
    locked_until    timestamptz NULL,
    created_at      timestamptz NOT NULL,
    last_login_at   timestamptz NULL,
    CONSTRAINT pk_admin_users PRIMARY KEY (id),
    CONSTRAINT ck_admin_users_status CHECK (status IN ('Active', 'Disabled'))
);

-- Admin sessions: only the SHA-256 of the refresh token is stored. A session dies with its account.
CREATE TABLE IF NOT EXISTS public.admin_refresh_tokens (
    id                   uuid        NOT NULL,
    admin_id             uuid        NOT NULL,
    token_hash           text        NOT NULL,
    family_id            uuid        NOT NULL,
    created_at           timestamptz NOT NULL,
    expires_at           timestamptz NOT NULL,
    family_expires_at    timestamptz NOT NULL,
    revoked_at           timestamptz NULL,
    replaced_by_token_id uuid        NULL,
    CONSTRAINT pk_admin_refresh_tokens PRIMARY KEY (id),
    CONSTRAINT fk_admin_refresh_tokens_admin_id FOREIGN KEY (admin_id) REFERENCES public.admin_users (id) ON DELETE CASCADE
);

-- Single use recovery codes: only a keyed hash (HMAC-SHA256 with the pepper) is stored.
CREATE TABLE IF NOT EXISTS public.admin_recovery_codes (
    id        uuid        NOT NULL,
    admin_id  uuid        NOT NULL,
    code_hash text        NOT NULL,
    used_at   timestamptz NULL,
    CONSTRAINT pk_admin_recovery_codes PRIMARY KEY (id),
    CONSTRAINT fk_admin_recovery_codes_admin_id FOREIGN KEY (admin_id) REFERENCES public.admin_users (id) ON DELETE CASCADE
);

-- "Taken care of": which flagged USER message, which admin, when. No free text, so nothing about the content can land here.
-- The row goes away with its message (retention, erasure). Reopening sets reopened_at; a new acknowledgement adds a new row.
CREATE TABLE IF NOT EXISTS public.crisis_acknowledgements (
    id                    uuid        NOT NULL,
    message_id            uuid        NOT NULL,
    admin_id              uuid        NOT NULL,
    acknowledged_at       timestamptz NOT NULL,
    reopened_at           timestamptz NULL,
    reopened_by_admin_id  uuid        NULL,
    CONSTRAINT pk_crisis_acknowledgements PRIMARY KEY (id),
    CONSTRAINT fk_crisis_acknowledgements_message_id FOREIGN KEY (message_id) REFERENCES public.messages (id) ON DELETE CASCADE,
    CONSTRAINT fk_crisis_acknowledgements_admin_id FOREIGN KEY (admin_id) REFERENCES public.admin_users (id) ON DELETE RESTRICT
);

-- ------------------------------------------------------------------ indexes

CREATE UNIQUE INDEX IF NOT EXISTS ux_admin_users_username ON public.admin_users (username);

CREATE UNIQUE INDEX IF NOT EXISTS ux_admin_refresh_tokens_token_hash ON public.admin_refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS ix_admin_refresh_tokens_family ON public.admin_refresh_tokens (family_id);
CREATE INDEX IF NOT EXISTS ix_admin_refresh_tokens_admin ON public.admin_refresh_tokens (admin_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_admin_recovery_codes_admin_hash ON public.admin_recovery_codes (admin_id, code_hash);

-- One OPEN acknowledgement per message (a reopened one no longer counts).
CREATE UNIQUE INDEX IF NOT EXISTS ux_crisis_acknowledgements_open_message ON public.crisis_acknowledgements (message_id) WHERE reopened_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_crisis_acknowledgements_message ON public.crisis_acknowledgements (message_id);

-- A label names one participant (empty and missing labels are not compared).
CREATE UNIQUE INDEX IF NOT EXISTS ux_participants_label ON public.participants (label) WHERE label IS NOT NULL AND label <> '';

-- The admin crisis list reads the flagged messages by time.
CREATE INDEX IF NOT EXISTS ix_messages_crisis_created ON public.messages (created_at) WHERE is_crisis;

-- Login counts and first/last login per participant: audit rows by actor, action and time.
CREATE INDEX IF NOT EXISTS ix_audit_logs_actor_action_time ON public.audit_logs (actor_id, action, "timestamp");

-- ------------------------------------------------------------------ version record

INSERT INTO public.app_schema_version (version, name) VALUES (3, 'admin')
ON CONFLICT (version) DO NOTHING;

COMMIT;
