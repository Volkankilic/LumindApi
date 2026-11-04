-- =====================================================================================
-- 002_retention_indexes_down.sql : undoes 002_retention_indexes.sql
-- =====================================================================================
-- Drops only the two indexes and the version-2 record. No table and no row of data is touched.
-- Afterwards the application expects version 2 no longer: set Database:ExpectedSchemaVersion back to 1
-- (or simply run 002_retention_indexes.sql again).
-- =====================================================================================

BEGIN;

DROP INDEX IF EXISTS public.ix_audit_logs_anonymous_timestamp;
DROP INDEX IF EXISTS public.ix_audit_logs_timestamp;

DELETE FROM public.app_schema_version WHERE version = 2 AND name = 'retention_indexes';

COMMIT;
