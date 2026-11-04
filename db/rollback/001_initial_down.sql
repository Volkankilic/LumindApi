-- =====================================================================================
-- 001_initial_down.sql : undoes 001_initial.sql
-- =====================================================================================
-- !!! DESTRUCTIVE !!! Drops EVERY table of the API with ALL its data (participants,
-- consent log, conversations, messages, tokens, audit log). There is no undo.
-- Only run it on purpose, on the one Railway database, when you want to start from zero.
-- Afterwards run 001_initial.sql again to rebuild the empty schema.
-- =====================================================================================

BEGIN;

DROP TABLE IF EXISTS public.messages;
DROP TABLE IF EXISTS public.conversations;
DROP TABLE IF EXISTS public.refresh_tokens;
DROP TABLE IF EXISTS public.consent_records;
DROP TABLE IF EXISTS public.audit_logs;
DROP TABLE IF EXISTS public.participants;
DROP TABLE IF EXISTS public.app_schema_version;

COMMIT;
