-- =====================================================================================
-- 003_admin_down.sql : undoes 003_admin.sql
-- =====================================================================================
-- DESTRUCTIVE for the admin tables: it DROPS admin_users, admin_refresh_tokens, admin_recovery_codes and
-- crisis_acknowledgements with everything in them (accounts, sessions, recovery codes, acknowledgements), and the indexes
-- added to participants, messages and audit_logs. No participant, conversation, message or audit ROW is touched.
-- Afterwards the application expects version 3 no longer: set Database:ExpectedSchemaVersion back to 2
-- (or simply run 003_admin.sql again; the admins must then be created again from the command line).
-- =====================================================================================

BEGIN;

DROP INDEX IF EXISTS public.ix_audit_logs_actor_action_time;
DROP INDEX IF EXISTS public.ix_messages_crisis_created;
DROP INDEX IF EXISTS public.ux_participants_label;

DROP TABLE IF EXISTS public.crisis_acknowledgements;
DROP TABLE IF EXISTS public.admin_recovery_codes;
DROP TABLE IF EXISTS public.admin_refresh_tokens;
DROP TABLE IF EXISTS public.admin_users;

DELETE FROM public.app_schema_version WHERE version = 3 AND name = 'admin';

COMMIT;
