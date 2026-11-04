-- =====================================================================================
-- 001_initial.sql : first schema of the Lumind API (participants, consent, chat, tokens, audit)
-- =====================================================================================
-- Purpose : creates every table, constraint and index the API needs. Plain PostgreSQL:
--           no extensions, no gen_random_uuid (the application generates ids), no roles,
--           no CREATE DATABASE, no passwords. No DO $$ blocks: runs in psql AND in GUI
--           tools such as DBeaver.
-- Where   : the single Railway PostgreSQL database (default name "railway"). Synthetic /
--           test data ONLY: real patient data never goes into this database.
-- Safe to run again: IDEMPOTENT (IF NOT EXISTS) and a SINGLE TRANSACTION (all or nothing).
--           Constraints live inside the CREATE TABLE statements, so they are created with
--           their table. If a table already exists it is left untouched.
-- Order   : run this file, then verify.sql, then (optionally) local/seed.sql.
-- Rollback: rollback/001_initial_down.sql (DESTRUCTIVE: drops every table).
-- Rule    : never edit this file after it ran successfully. A change is a new numbered
--           file (002_*.sql).
-- Note    : the column "timestamp" is quoted because it is also a type name.
-- =====================================================================================

BEGIN;

-- Schema version bookkeeping (the API reads this at startup, read-only).
CREATE TABLE IF NOT EXISTS public.app_schema_version (
    version    integer     NOT NULL,
    name       text        NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_app_schema_version PRIMARY KEY (version)
);

-- Participants. The code is never stored, only its HMAC-SHA256 (code_hash).
CREATE TABLE IF NOT EXISTS public.participants (
    id           uuid        NOT NULL,
    code_hash    text        NOT NULL,
    label        text        NULL,
    status       text        NOT NULL,
    nickname     text        NULL,
    created_at   timestamptz NOT NULL,
    last_seen_at timestamptz NULL,
    CONSTRAINT pk_participants PRIMARY KEY (id),
    CONSTRAINT ck_participants_status CHECK (status IN ('Invited', 'Active', 'Revoked', 'Deleted'))
);

-- Consent log: append-only. The latest row of a type is its current state.
-- "Notice read" is an event, never a consent that can be refused.
CREATE TABLE IF NOT EXISTS public.consent_records (
    id               uuid        NOT NULL,
    participant_id   uuid        NOT NULL,
    type             text        NOT NULL,
    document_version text        NOT NULL,
    granted          boolean     NOT NULL,
    "timestamp"      timestamptz NOT NULL,
    CONSTRAINT pk_consent_records PRIMARY KEY (id),
    CONSTRAINT ck_consent_records_type CHECK (type IN ('PrivacyNotice', 'ExplicitConsent', 'ResearchUse')),
    CONSTRAINT ck_consent_records_notice_granted CHECK (type <> 'PrivacyNotice' OR granted),
    CONSTRAINT fk_consent_records_participant_id FOREIGN KEY (participant_id) REFERENCES public.participants (id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS public.conversations (
    id             uuid        NOT NULL,
    participant_id uuid        NOT NULL,
    started_at     timestamptz NOT NULL,
    ended_at       timestamptz NULL,
    peplau_phase   text        NOT NULL DEFAULT 'Orientation',
    CONSTRAINT pk_conversations PRIMARY KEY (id),
    CONSTRAINT ck_conversations_peplau_phase CHECK (peplau_phase IN ('Orientation', 'Identification', 'Exploitation', 'Resolution')),
    CONSTRAINT fk_conversations_participant_id FOREIGN KEY (participant_id) REFERENCES public.participants (id) ON DELETE RESTRICT
);

-- Deleting a conversation deletes its messages.
CREATE TABLE IF NOT EXISTS public.messages (
    id                    uuid        NOT NULL,
    conversation_id       uuid        NOT NULL,
    role                  text        NOT NULL,
    content               text        NOT NULL,
    created_at            timestamptz NOT NULL,
    is_crisis             boolean     NOT NULL DEFAULT FALSE,
    client_message_id     uuid        NULL,
    completion            text        NULL,
    system_prompt_version text        NULL,
    CONSTRAINT pk_messages PRIMARY KEY (id),
    CONSTRAINT ck_messages_role CHECK (role IN ('User', 'Assistant')),
    CONSTRAINT ck_messages_completion CHECK (completion IS NULL OR completion IN ('Complete', 'Stopped', 'Incomplete')),
    CONSTRAINT ck_messages_completion_assistant_only CHECK (completion IS NULL OR role = 'Assistant'),
    CONSTRAINT fk_messages_conversation_id FOREIGN KEY (conversation_id) REFERENCES public.conversations (id) ON DELETE CASCADE
);

-- Refresh tokens: only the SHA-256 of the token is stored.
CREATE TABLE IF NOT EXISTS public.refresh_tokens (
    id                   uuid        NOT NULL,
    participant_id       uuid        NOT NULL,
    token_hash           text        NOT NULL,
    family_id            uuid        NOT NULL,
    created_at           timestamptz NOT NULL,
    expires_at           timestamptz NOT NULL,
    family_expires_at    timestamptz NOT NULL,
    revoked_at           timestamptz NULL,
    replaced_by_token_id uuid        NULL,
    CONSTRAINT pk_refresh_tokens PRIMARY KEY (id),
    CONSTRAINT fk_refresh_tokens_participant_id FOREIGN KEY (participant_id) REFERENCES public.participants (id) ON DELETE RESTRICT
);

-- Audit log: who did what. Never content (no message text, nickname, code or raw IP).
-- actor_id has NO foreign key on purpose: entries outlive a deleted participant.
CREATE TABLE IF NOT EXISTS public.audit_logs (
    id          uuid        NOT NULL,
    actor_type  text        NOT NULL,
    actor_id    uuid        NULL,
    action      text        NOT NULL,
    target_type text        NULL,
    target_id   uuid        NULL,
    "timestamp" timestamptz NOT NULL,
    ip_hash     text        NULL,
    CONSTRAINT pk_audit_logs PRIMARY KEY (id),
    CONSTRAINT ck_audit_logs_actor_type CHECK (actor_type IN ('Participant', 'Researcher', 'System', 'Anonymous'))
);

-- ------------------------------------------------------------------ indexes

CREATE UNIQUE INDEX IF NOT EXISTS ux_participants_code_hash ON public.participants (code_hash);
CREATE INDEX IF NOT EXISTS ix_participants_label ON public.participants (label);

CREATE INDEX IF NOT EXISTS ix_consent_records_participant_type_time ON public.consent_records (participant_id, type, "timestamp" DESC);

-- At most ONE active conversation per participant, enforced by the database (also under races).
CREATE UNIQUE INDEX IF NOT EXISTS ux_conversations_one_active_per_participant ON public.conversations (participant_id) WHERE ended_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_conversations_participant_started ON public.conversations (participant_id, started_at DESC);

-- Idempotency: one client message id per conversation (user messages only; others are NULL).
CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_conversation_client_message ON public.messages (conversation_id, client_message_id) WHERE client_message_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_messages_conversation_created ON public.messages (conversation_id, created_at);

CREATE UNIQUE INDEX IF NOT EXISTS ux_refresh_tokens_token_hash ON public.refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_family ON public.refresh_tokens (family_id);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_participant ON public.refresh_tokens (participant_id);

CREATE INDEX IF NOT EXISTS ix_audit_logs_actor_time ON public.audit_logs (actor_id, "timestamp");

-- ------------------------------------------------------------------ version record

INSERT INTO public.app_schema_version (version, name) VALUES (1, 'initial')
ON CONFLICT (version) DO NOTHING;

COMMIT;
