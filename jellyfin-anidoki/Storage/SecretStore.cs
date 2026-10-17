using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Storage;

/// <summary>Reads and replaces stored credentials. Callers must never log the values.</summary>
public sealed class SecretStore(AniDokiDatabase database) {
    /// <exception cref="System.InvalidOperationException">The database is unavailable.</exception>
    public Dictionary<SecretKey, string> ReadAll() {
        var secrets = new Dictionary<SecretKey, string>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, owner, provider, value FROM secrets";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            secrets[new SecretKey(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = reader.GetString(3);
        return secrets;
    }

    /// <summary>Makes the stored credentials exactly <paramref name="secrets"/>, in one transaction.</summary>
    /// <exception cref="System.InvalidOperationException">The database is unavailable.</exception>
    public void ReplaceAll(IReadOnlyDictionary<SecretKey, string> secrets) {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM secrets";
            clear.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand()) {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO secrets (kind, owner, provider, value) VALUES ($kind, $owner, $provider, $value)";
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var owner = insert.Parameters.Add("$owner", SqliteType.Text);
            var provider = insert.Parameters.Add("$provider", SqliteType.Text);
            var value = insert.Parameters.Add("$value", SqliteType.Text);
            foreach (var (key, secret) in secrets) {
                kind.Value = key.Kind;
                owner.Value = key.Owner;
                provider.Value = key.Provider;
                value.Value = secret;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }
}
