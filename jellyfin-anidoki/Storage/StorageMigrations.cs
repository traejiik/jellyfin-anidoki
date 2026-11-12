namespace jellyfin_anidoki.Storage;

/// <summary>
/// Forward-only schema changes, applied in order on open. Never edit a released migration; add a new one.
/// </summary>
internal static class StorageMigrations {
    public static readonly (int Version, string Sql)[] All = [
        (1, """
            CREATE TABLE secrets (
                kind TEXT NOT NULL,
                owner TEXT NOT NULL,
                provider TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (kind, owner, provider)
            );
            """),
        (2, """
            CREATE TABLE manual_mark_enrollments (
                user_id TEXT NOT NULL,
                authentication_key TEXT NOT NULL,
                PRIMARY KEY (user_id, authentication_key)
            );
            """),
        (3, """
            CREATE TABLE operations (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                source TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                started_utc TEXT,
                finished_utc TEXT,
                message TEXT,
                done INTEGER NOT NULL DEFAULT 0,
                total INTEGER
            );
            CREATE INDEX operations_by_user ON operations (user_id, kind, state);
            CREATE TABLE operation_actions (
                operation_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                provider TEXT NOT NULL,
                target_id TEXT NOT NULL,
                target_title TEXT NOT NULL,
                outcome TEXT NOT NULL,
                step TEXT NOT NULL,
                progress INTEGER,
                status TEXT,
                meaningful INTEGER NOT NULL,
                PRIMARY KEY (operation_id, seq)
            );
            """),
        (4, """
            ALTER TABLE operation_actions ADD COLUMN source_item TEXT;
            ALTER TABLE operation_actions ADD COLUMN source_episode INTEGER;
            ALTER TABLE operation_actions ADD COLUMN target_episode INTEGER;
            ALTER TABLE operation_actions ADD COLUMN resolved_by TEXT;
            ALTER TABLE operation_actions ADD COLUMN resolution_note TEXT;
            ALTER TABLE operation_actions ADD COLUMN planned_change TEXT;
            ALTER TABLE operation_actions ADD COLUMN planned_against TEXT;
            CREATE TABLE schema_compat (min_reader INTEGER NOT NULL);
            INSERT INTO schema_compat (min_reader) VALUES (4);
            """),
        (5, """
            CREATE TABLE match_sources (
                source_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE matches (
                id TEXT PRIMARY KEY,
                source_key TEXT NOT NULL REFERENCES match_sources (source_key),
                provider TEXT NOT NULL,
                owner_user_id TEXT,
                revision INTEGER NOT NULL,
                base_shared_revision INTEGER,
                connection_generation TEXT,
                note TEXT,
                updated_by TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX matches_by_scope ON matches (source_key, provider, COALESCE(owner_user_id, ''));
            CREATE TABLE match_ranges (
                match_id TEXT NOT NULL REFERENCES matches (id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                source_start INTEGER NOT NULL,
                source_end INTEGER,
                target_id INTEGER NOT NULL,
                target_alternative_id TEXT,
                target_lookup TEXT,
                target_title TEXT NOT NULL,
                target_start INTEGER NOT NULL,
                PRIMARY KEY (match_id, seq)
            );
            CREATE TABLE match_exceptions (
                match_id TEXT NOT NULL REFERENCES matches (id) ON DELETE CASCADE,
                source_episode INTEGER NOT NULL,
                kind TEXT NOT NULL,
                target_id INTEGER,
                target_alternative_id TEXT,
                target_lookup TEXT,
                target_title TEXT,
                target_episode INTEGER,
                PRIMARY KEY (match_id, source_episode)
            );
            CREATE TABLE match_revisions (
                source_key TEXT NOT NULL,
                provider TEXT NOT NULL,
                owner_user_id TEXT NOT NULL DEFAULT '',
                revision INTEGER NOT NULL,
                action TEXT NOT NULL,
                author TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                snapshot TEXT NOT NULL,
                promoted_from TEXT,
                PRIMARY KEY (source_key, provider, owner_user_id, revision)
            );
            CREATE TABLE resolution_check_rows (
                operation_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                source_key TEXT NOT NULL,
                provider TEXT NOT NULL,
                automatic TEXT NOT NULL,
                saved TEXT,
                played_rule_progress INTEGER,
                highest_watched_progress INTEGER,
                differs INTEGER NOT NULL,
                PRIMARY KEY (operation_id, seq)
            );
            ALTER TABLE operation_actions ADD COLUMN match_revision INTEGER;
            """),
        (6, """
            CREATE TABLE preview_rows (
                operation_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                direction TEXT NOT NULL,
                provider TEXT NOT NULL,
                source_key TEXT NOT NULL,
                source_item TEXT NOT NULL,
                source_title TEXT NOT NULL,
                source_episode INTEGER,
                target_id TEXT NOT NULL,
                target_title TEXT NOT NULL,
                target_episode INTEGER,
                kind TEXT NOT NULL,
                reason TEXT NOT NULL,
                resolved_by TEXT,
                resolution_note TEXT,
                match_revision INTEGER,
                apply_saved_matches INTEGER NOT NULL,
                watched_fingerprint TEXT,
                planned_change TEXT,
                planned_against TEXT,
                selectable INTEGER NOT NULL,
                selected_by_default INTEGER NOT NULL,
                applied_operation_id TEXT,
                PRIMARY KEY (operation_id, seq)
            );
            ALTER TABLE operations ADD COLUMN preview_of TEXT;
            """)
    ];

    public static int Latest => All[^1].Version;
}
