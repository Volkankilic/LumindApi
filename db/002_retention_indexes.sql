-- =====================================================================================
-- 002_retention_indexes.sql : indexes for the retention job (audit_logs by time)
-- =====================================================================================
-- Purpose : the retention job (Privacy:RetentionDays, off by default) deletes audit rows older than a cutoff.
--           Without these indexes every run scans the whole audit_logs table, which only ever grows.
--           Only indexes: no column, no data and no constraint changes. Safe on a table that already has rows.
-- Where   : the single Railway PostgreSQL database. Run it AFTER 001_initial.sql and verify.sql.
-- Safe to run again: IDEMPOTENT (IF NOT EXISTS), a SINGLE TRANSACTION (all or nothing).
-- Order   : run this file, then verify.sql (it now expects schema version 2).
-- Rollback: rollback/002_retention_indexes_down.sql (drops only these two indexes).
-- Rule    : never edit this file after it ran successfully. A change is a new numbered file.
-- Note    : the column "timestamp" is quoted because it is also a type name.
-- =====================================================================================

BEGIN;

-- Audit rows that belong to a known actor (and system rows): "older than the cutoff".
CREATE INDEX IF NOT EXISTS ix_audit_logs_timestamp ON public.audit_logs ("timestamp");

-- Rows of unknown actors (failed code attempts): the rule with the shortest period, and the largest group.
CREATE INDEX IF NOT EXISTS ix_audit_logs_anonymous_timestamp ON public.audit_logs ("timestamp") WHERE actor_type = 'Anonymous';

-- ------------------------------------------------------------------ version record

INSERT INTO public.app_schema_version (version, name) VALUES (2, 'retention_indexes')
ON CONFLICT (version) DO NOTHING;

COMMIT;
