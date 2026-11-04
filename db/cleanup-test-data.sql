-- =====================================================================================
-- cleanup-test-data.sql : removes leftovers of crashed integration tests
-- =====================================================================================
-- Integration tests create participants whose label starts with 'itest-' and delete them
-- when they finish. If a test run crashes, rows can be left behind: this script removes
-- ONLY participants labelled 'itest-...' and the rows that belong to them (tokens, consent,
-- conversations and their messages, audit entries). Seed participants ('seed-...') and every
-- other row are left alone. Run it by hand whenever you like; it is safe to repeat.
-- =====================================================================================

BEGIN;

CREATE TEMP TABLE itest_participants ON COMMIT DROP AS
    SELECT id FROM public.participants WHERE starts_with(label, 'itest-');

-- Child rows first (foreign keys), then the participants. Messages go with their conversation.
DELETE FROM public.audit_logs
    WHERE actor_id IN (SELECT id FROM itest_participants)
       OR target_id IN (SELECT id FROM itest_participants);
DELETE FROM public.refresh_tokens  WHERE participant_id IN (SELECT id FROM itest_participants);
DELETE FROM public.consent_records WHERE participant_id IN (SELECT id FROM itest_participants);
DELETE FROM public.conversations   WHERE participant_id IN (SELECT id FROM itest_participants);
DELETE FROM public.participants    WHERE id IN (SELECT id FROM itest_participants);

COMMIT;
