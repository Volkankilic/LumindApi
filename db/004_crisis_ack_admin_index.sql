-- =====================================================================================
-- 004_crisis_ack_admin_index.sql : index on crisis_acknowledgements.admin_id
-- =====================================================================================
-- Purpose : the foreign key crisis_acknowledgements.admin_id -> admin_users(id) (ON DELETE RESTRICT) had no index, while the EF model
--           (like every foreign key in it) expects one. The index also serves the check PostgreSQL makes when an admin row is deleted.
--           Only an index: no column, no data, no constraint change. Safe on a table that already has rows.
-- Where   : the single Railway PostgreSQL database. Run it AFTER 003_admin.sql and verify.sql.
-- Safe to run again: IDEMPOTENT (IF NOT EXISTS), a SINGLE TRANSACTION (all or nothing).
-- Order   : run this file, then verify.sql (it now expects schema version 4).
-- Rollback: rollback/004_crisis_ack_admin_index_down.sql (drops only this index).
-- Rule    : never edit this file after it ran successfully. A change is a new numbered file.
-- =====================================================================================

BEGIN;

CREATE INDEX IF NOT EXISTS ix_crisis_acknowledgements_admin ON public.crisis_acknowledgements (admin_id);

-- ------------------------------------------------------------------ version record

INSERT INTO public.app_schema_version (version, name) VALUES (4, 'crisis_ack_admin_index')
ON CONFLICT (version) DO NOTHING;

COMMIT;
