-- =====================================================================================
-- verify.sql : READ-ONLY check that 001_initial.sql, 002_retention_indexes.sql and 003_admin.sql were applied correctly
-- =====================================================================================
-- Only SELECTs: changes nothing. Run it after 001_initial.sql (and again after any later script).
-- It returns ONE result table: one row per check, column "ok" is true/false, and the last
-- row ("SUMMARY") says ALL OK or how many checks failed. Everything should be true.
-- =====================================================================================

WITH expected_columns (table_name, column_name, data_type, is_nullable) AS (
    VALUES
    ('participants',    'id',                    'uuid',                     'NO'),
    ('participants',    'code_hash',             'text',                     'NO'),
    ('participants',    'label',                 'text',                     'YES'),
    ('participants',    'status',                'text',                     'NO'),
    ('participants',    'nickname',              'text',                     'YES'),
    ('participants',    'created_at',            'timestamp with time zone', 'NO'),
    ('participants',    'last_seen_at',          'timestamp with time zone', 'YES'),
    ('consent_records', 'id',                    'uuid',                     'NO'),
    ('consent_records', 'participant_id',        'uuid',                     'NO'),
    ('consent_records', 'type',                  'text',                     'NO'),
    ('consent_records', 'document_version',      'text',                     'NO'),
    ('consent_records', 'granted',               'boolean',                  'NO'),
    ('consent_records', 'timestamp',             'timestamp with time zone', 'NO'),
    ('conversations',   'id',                    'uuid',                     'NO'),
    ('conversations',   'participant_id',        'uuid',                     'NO'),
    ('conversations',   'started_at',            'timestamp with time zone', 'NO'),
    ('conversations',   'ended_at',              'timestamp with time zone', 'YES'),
    ('conversations',   'peplau_phase',          'text',                     'NO'),
    ('messages',        'id',                    'uuid',                     'NO'),
    ('messages',        'conversation_id',       'uuid',                     'NO'),
    ('messages',        'role',                  'text',                     'NO'),
    ('messages',        'content',               'text',                     'NO'),
    ('messages',        'created_at',            'timestamp with time zone', 'NO'),
    ('messages',        'is_crisis',             'boolean',                  'NO'),
    ('messages',        'client_message_id',     'uuid',                     'YES'),
    ('messages',        'completion',            'text',                     'YES'),
    ('messages',        'system_prompt_version', 'text',                     'YES'),
    ('refresh_tokens',  'id',                    'uuid',                     'NO'),
    ('refresh_tokens',  'participant_id',        'uuid',                     'NO'),
    ('refresh_tokens',  'token_hash',            'text',                     'NO'),
    ('refresh_tokens',  'family_id',             'uuid',                     'NO'),
    ('refresh_tokens',  'created_at',            'timestamp with time zone', 'NO'),
    ('refresh_tokens',  'expires_at',            'timestamp with time zone', 'NO'),
    ('refresh_tokens',  'family_expires_at',     'timestamp with time zone', 'NO'),
    ('refresh_tokens',  'revoked_at',            'timestamp with time zone', 'YES'),
    ('refresh_tokens',  'replaced_by_token_id',  'uuid',                     'YES'),
    ('audit_logs',      'id',                    'uuid',                     'NO'),
    ('audit_logs',      'actor_type',            'text',                     'NO'),
    ('audit_logs',      'actor_id',              'uuid',                     'YES'),
    ('audit_logs',      'action',                'text',                     'NO'),
    ('audit_logs',      'target_type',           'text',                     'YES'),
    ('audit_logs',      'target_id',             'uuid',                     'YES'),
    ('audit_logs',      'timestamp',             'timestamp with time zone', 'NO'),
    ('audit_logs',      'ip_hash',               'text',                     'YES'),
    -- 003_admin.sql
    ('admin_users', 'id', 'uuid', 'NO'),
    ('admin_users', 'username', 'text', 'NO'),
    ('admin_users', 'password_hash', 'text', 'NO'),
    ('admin_users', 'totp_secret', 'text', 'YES'),
    ('admin_users', 'totp_enabled', 'boolean', 'NO'),
    ('admin_users', 'totp_last_step', 'bigint', 'NO'),
    ('admin_users', 'status', 'text', 'NO'),
    ('admin_users', 'failed_attempts', 'integer', 'NO'),
    ('admin_users', 'locked_until', 'timestamp with time zone', 'YES'),
    ('admin_users', 'created_at', 'timestamp with time zone', 'NO'),
    ('admin_users', 'last_login_at', 'timestamp with time zone', 'YES'),
    ('admin_refresh_tokens', 'id', 'uuid', 'NO'),
    ('admin_refresh_tokens', 'admin_id', 'uuid', 'NO'),
    ('admin_refresh_tokens', 'token_hash', 'text', 'NO'),
    ('admin_refresh_tokens', 'family_id', 'uuid', 'NO'),
    ('admin_refresh_tokens', 'created_at', 'timestamp with time zone', 'NO'),
    ('admin_refresh_tokens', 'expires_at', 'timestamp with time zone', 'NO'),
    ('admin_refresh_tokens', 'family_expires_at', 'timestamp with time zone', 'NO'),
    ('admin_refresh_tokens', 'revoked_at', 'timestamp with time zone', 'YES'),
    ('admin_refresh_tokens', 'replaced_by_token_id', 'uuid', 'YES'),
    ('admin_recovery_codes', 'id', 'uuid', 'NO'),
    ('admin_recovery_codes', 'admin_id', 'uuid', 'NO'),
    ('admin_recovery_codes', 'code_hash', 'text', 'NO'),
    ('admin_recovery_codes', 'used_at', 'timestamp with time zone', 'YES'),
    ('crisis_acknowledgements', 'id', 'uuid', 'NO'),
    ('crisis_acknowledgements', 'message_id', 'uuid', 'NO'),
    ('crisis_acknowledgements', 'admin_id', 'uuid', 'NO'),
    ('crisis_acknowledgements', 'acknowledged_at', 'timestamp with time zone', 'NO'),
    ('crisis_acknowledgements', 'reopened_at', 'timestamp with time zone', 'YES'),
    ('crisis_acknowledgements', 'reopened_by_admin_id', 'uuid', 'YES')
),
expected_tables (table_name) AS (
    VALUES ('app_schema_version'), ('participants'), ('consent_records'), ('conversations'),
           ('messages'), ('refresh_tokens'), ('audit_logs'),
           ('admin_users'), ('admin_refresh_tokens'), ('admin_recovery_codes'), ('crisis_acknowledgements')
),
-- Indexes by name. "pattern" is matched against pg_indexes.indexdef (NULL = existence only).
expected_indexes (index_name, pattern) AS (
    VALUES
    ('ux_participants_code_hash',                    '%UNIQUE%(code_hash)%'),
    ('ix_participants_label',                        '%(label)%'),
    ('ix_consent_records_participant_type_time',     '%(participant_id, type, %timestamp%DESC)%'),
    ('ux_conversations_one_active_per_participant',  '%UNIQUE%(participant_id)%WHERE (ended_at IS NULL)%'),
    ('ix_conversations_participant_started',         '%(participant_id, started_at DESC)%'),
    ('ux_messages_conversation_client_message',      '%UNIQUE%(conversation_id, client_message_id)%WHERE (client_message_id IS NOT NULL)%'),
    ('ix_messages_conversation_created',             '%(conversation_id, created_at)%'),
    ('ux_refresh_tokens_token_hash',                 '%UNIQUE%(token_hash)%'),
    ('ix_refresh_tokens_family',                     '%(family_id)%'),
    ('ix_refresh_tokens_participant',                '%(participant_id)%'),
    ('ix_audit_logs_actor_time',                     '%(actor_id, %timestamp%)%'),
    -- 002_retention_indexes.sql
    ('ix_audit_logs_timestamp',                      '%("timestamp")%'),
    ('ix_audit_logs_anonymous_timestamp',            '%("timestamp")%WHERE (actor_type = ''Anonymous''%'),
    -- 003_admin.sql
    ('ux_admin_users_username', '%UNIQUE%(username)%'),
    ('ux_admin_refresh_tokens_token_hash', '%UNIQUE%(token_hash)%'),
    ('ix_admin_refresh_tokens_family', '%(family_id)%'),
    ('ix_admin_refresh_tokens_admin', '%(admin_id)%'),
    ('ux_admin_recovery_codes_admin_hash', '%UNIQUE%(admin_id, code_hash)%'),
    ('ux_crisis_acknowledgements_open_message', '%UNIQUE%(message_id)%WHERE (reopened_at IS NULL)%'),
    ('ix_crisis_acknowledgements_message', '%(message_id)%'),
    ('ux_participants_label', '%UNIQUE%(label)%WHERE%label IS NOT NULL%'),
    ('ix_messages_crisis_created', '%(created_at)%WHERE is_crisis%'),
    ('ix_audit_logs_actor_action_time', '%(actor_id, action, %timestamp%)%')
),
expected_constraints (constraint_name, table_name, kind) AS (
    VALUES
    ('pk_app_schema_version',                  'app_schema_version', 'p'),
    ('pk_participants',                        'participants',       'p'),
    ('pk_consent_records',                     'consent_records',    'p'),
    ('pk_conversations',                       'conversations',      'p'),
    ('pk_messages',                            'messages',           'p'),
    ('pk_refresh_tokens',                      'refresh_tokens',     'p'),
    ('pk_audit_logs',                          'audit_logs',         'p'),
    ('ck_participants_status',                 'participants',       'c'),
    ('ck_consent_records_type',                'consent_records',    'c'),
    ('ck_consent_records_notice_granted',      'consent_records',    'c'),
    ('ck_conversations_peplau_phase',          'conversations',      'c'),
    ('ck_messages_role',                       'messages',           'c'),
    ('ck_messages_completion',                 'messages',           'c'),
    ('ck_messages_completion_assistant_only',  'messages',           'c'),
    ('ck_audit_logs_actor_type',               'audit_logs',         'c'),
    ('fk_consent_records_participant_id',      'consent_records',    'f'),
    ('fk_conversations_participant_id',        'conversations',      'f'),
    ('fk_messages_conversation_id',            'messages',           'f'),
    ('fk_refresh_tokens_participant_id',       'refresh_tokens',     'f'),
    -- 003_admin.sql
    ('pk_admin_users', 'admin_users', 'p'),
    ('pk_admin_refresh_tokens', 'admin_refresh_tokens', 'p'),
    ('pk_admin_recovery_codes', 'admin_recovery_codes', 'p'),
    ('pk_crisis_acknowledgements', 'crisis_acknowledgements', 'p'),
    ('ck_admin_users_status', 'admin_users', 'c'),
    ('fk_admin_refresh_tokens_admin_id', 'admin_refresh_tokens', 'f'),
    ('fk_admin_recovery_codes_admin_id', 'admin_recovery_codes', 'f'),
    ('fk_crisis_acknowledgements_message_id', 'crisis_acknowledgements', 'f'),
    ('fk_crisis_acknowledgements_admin_id', 'crisis_acknowledgements', 'f')
),
checks AS (
    SELECT 1 AS sort, 'table ' || t.table_name AS check_name,
           EXISTS (SELECT 1 FROM information_schema.tables x
                   WHERE x.table_schema = 'public' AND x.table_name = t.table_name) AS ok
    FROM expected_tables t

    UNION ALL
    SELECT 2, 'column ' || c.table_name || '.' || c.column_name || ' (' || c.data_type || ', nullable ' || c.is_nullable || ')',
           EXISTS (SELECT 1 FROM information_schema.columns x
                   WHERE x.table_schema = 'public' AND x.table_name = c.table_name
                     AND x.column_name = c.column_name AND x.data_type = c.data_type
                     AND x.is_nullable = c.is_nullable)
    FROM expected_columns c

    UNION ALL
    SELECT 3, 'constraint ' || k.constraint_name,
           EXISTS (SELECT 1 FROM pg_constraint x
                   JOIN pg_class r ON r.oid = x.conrelid
                   JOIN pg_namespace n ON n.oid = r.relnamespace
                   WHERE n.nspname = 'public' AND r.relname = k.table_name
                     AND x.conname = k.constraint_name AND x.contype = k.kind)
    FROM expected_constraints k

    UNION ALL
    SELECT 4, 'index ' || i.index_name,
           EXISTS (SELECT 1 FROM pg_indexes x
                   WHERE x.schemaname = 'public' AND x.indexname = i.index_name
                     AND x.indexdef LIKE i.pattern)
    FROM expected_indexes i

    UNION ALL
    -- The two partial unique indexes that carry the business rules, spelled out.
    SELECT 5, 'rule: one active conversation per participant (partial unique index WHERE ended_at IS NULL)',
           EXISTS (SELECT 1 FROM pg_indexes
                   WHERE schemaname = 'public' AND tablename = 'conversations'
                     AND indexdef LIKE '%UNIQUE%(participant_id)%WHERE (ended_at IS NULL)%')
    UNION ALL
    SELECT 5, 'rule: client message id unique per conversation (partial unique index WHERE client_message_id IS NOT NULL)',
           EXISTS (SELECT 1 FROM pg_indexes
                   WHERE schemaname = 'public' AND tablename = 'messages'
                     AND indexdef LIKE '%UNIQUE%(conversation_id, client_message_id)%WHERE (client_message_id IS NOT NULL)%')

    UNION ALL
    SELECT 6, 'schema version 1 (initial) is recorded',
           COALESCE((SELECT true FROM public.app_schema_version WHERE version = 1 AND name = 'initial'), false)

    UNION ALL
    SELECT 6, 'schema version 2 (retention_indexes) is recorded',
           COALESCE((SELECT true FROM public.app_schema_version WHERE version = 2 AND name = 'retention_indexes'), false)

    UNION ALL
    SELECT 6, 'schema version 3 (admin) is recorded',
           COALESCE((SELECT true FROM public.app_schema_version WHERE version = 3 AND name = 'admin'), false)
),
report AS (
    SELECT sort, check_name, ok FROM checks
    UNION ALL
    SELECT 9, 'SUMMARY: ' || CASE WHEN count(*) FILTER (WHERE NOT ok) = 0
                                  THEN 'ALL OK (' || count(*) || ' checks)'
                                  ELSE count(*) FILTER (WHERE NOT ok) || ' of ' || count(*) || ' checks FAILED' END,
           count(*) FILTER (WHERE NOT ok) = 0
    FROM checks
)
SELECT ok, check_name FROM report ORDER BY sort, ok, check_name;

-- Extra information (run separately if you like): which versions are recorded.
-- SELECT version, name, applied_at FROM public.app_schema_version ORDER BY version;
