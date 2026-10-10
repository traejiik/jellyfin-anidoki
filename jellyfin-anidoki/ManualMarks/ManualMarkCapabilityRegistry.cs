#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace jellyfin_anidoki.ManualMarks;

// Sticky enrollment is deliberately separate from the ephemeral operation store.
public sealed class ManualMarkCapabilityRegistry
{
    private sealed record Enrollment(Guid UserId, string AuthenticationKey);
    private sealed record Document(int Version, Enrollment[] Identities);
    private readonly object _lock = new();
    private readonly string _path;
    private HashSet<Enrollment> _identities = [];
    public bool PersistenceUnreadable { get; private set; }
    public ManualMarkCapabilityRegistry(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<Document>(stream);
            if (document?.Version != 1 || document.Identities == null || document.Identities.Length > 10000 ||
                document.Identities.Any(e => e == null || e.UserId == Guid.Empty || string.IsNullOrWhiteSpace(e.AuthenticationKey))) throw new InvalidDataException();
            _identities = document.Identities.ToHashSet();
        }
        catch (FileNotFoundException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { PersistenceUnreadable = true; }
    }
    public bool RequiresConfirmation(Guid userId, string key)
    {
        lock (_lock) return PersistenceUnreadable || _identities.Contains(new(userId, key));
    }
    public bool HasEnrollmentForUser(Guid userId)
    {
        lock (_lock) return PersistenceUnreadable || _identities.Any(e => e.UserId == userId);
    }
    public bool Enroll(Guid userId, string key)
    {
        lock (_lock)
        {
            if (PersistenceUnreadable || userId == Guid.Empty || string.IsNullOrWhiteSpace(key)) return false;
            var identity = new Enrollment(userId, key);
            if (_identities.Contains(identity)) return true;
            if (_identities.Count >= 10000) return false;
            var updated = new HashSet<Enrollment>(_identities) { identity };
            return Persist(updated);
        }
    }
    public bool RemoveUser(Guid userId)
    {
        lock (_lock)
        {
            if (PersistenceUnreadable) return false;
            return Persist(_identities.Where(e => e.UserId != userId).ToHashSet());
        }
    }
    private bool Persist(HashSet<Enrollment> updated)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Document(1, updated.ToArray())); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _identities = updated; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
