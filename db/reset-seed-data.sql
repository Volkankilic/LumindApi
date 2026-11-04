-- =====================================================================================
-- reset-seed-data.sql : removes the seed participants ('seed-...') and everything that belongs to them
-- =====================================================================================
-- Use it after the manual release smoke test (docs/yayin-duman-testi.md), which signs in as the seed
-- participants and so changes them. Then run local/seed.sql again to put them back, then verify.sql.
-- DATA only (no DDL). It touches ONLY participants whose label starts with 'seed-' and their child rows:
-- tokens, consent, conversations and their messages (cascade), audit entries. Participants labelled
-- 'itest-...' and every other row are left alone. Safe to repeat. Run it by hand, in a single transaction.
-- =====================================================================================

BEGIN;

CREATE TEMP TABLE seed_participants ON COMMIT DROP AS
    SELECT id FROM public.participants WHERE starts_with(label, 'seed-');

-- Child rows first (foreign keys), then the participants. Messages go with their conversation.
DELETE FROM public.audit_logs
    WHERE actor_id IN (SELECT id FROM seed_participants)
       OR target_id IN (SELECT id FROM seed_participants);
DELETE FROM public.refresh_tokens  WHERE participant_id IN (SELECT id FROM seed_participants);
DELETE FROM public.consent_records WHERE participant_id IN (SELECT id FROM seed_participants);
DELETE FROM public.conversations   WHERE participant_id IN (SELECT id FROM seed_participants);
DELETE FROM public.participants    WHERE id IN (SELECT id FROM seed_participants);

COMMIT;
