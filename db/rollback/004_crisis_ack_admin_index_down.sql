-- =====================================================================================
-- 004_crisis_ack_admin_index_down.sql : undoes 004_crisis_ack_admin_index.sql
-- =====================================================================================
-- Drops only the index and the version-4 record. No table and no row of data is touched.
-- Afterwards the application expects version 4 no longer: set Database:ExpectedSchemaVersion back to 3
-- (or simply run 004_crisis_ack_admin_index.sql again).
-- =====================================================================================

BEGIN;

DROP INDEX IF EXISTS public.ix_crisis_acknowledgements_admin;

DELETE FROM public.app_schema_version WHERE version = 4 AND name = 'crisis_ack_admin_index';

COMMIT;
