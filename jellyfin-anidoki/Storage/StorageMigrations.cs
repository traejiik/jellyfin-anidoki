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
            """)
    ];

    public static int Latest => All[^1].Version;
}
