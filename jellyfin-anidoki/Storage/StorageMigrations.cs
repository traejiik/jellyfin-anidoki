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
            """)
    ];

    public static int Latest => All[^1].Version;
}
