using System;
using System.Collections.Generic;
using System.Threading;
using jellyfin_anidoki.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Operations;

/// <summary>
/// What running work can see and record. Recording never throws: a sync must not fail because its history could
/// not be written.
/// </summary>
public sealed class OperationContext {
    private readonly OperationStore _store;
    private readonly ILogger _logger;
    private readonly List<ProviderWriteOutcome> _outcomes = new();

    internal OperationContext(Guid operationId, Guid userId, OperationStore store, ILogger logger, CancellationToken cancellationToken) {
        OperationId = operationId;
        UserId = userId;
        _store = store;
        _logger = logger;
        CancellationToken = cancellationToken;
    }

    public Guid OperationId { get; }

    public Guid UserId { get; }

    /// <summary>Cancelled when someone cancels the operation or Jellyfin stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Overrides the result derived from outcomes, for work that records none.</summary>
    public OperationState? Result { get; set; }

    /// <summary>A short explanation stored with the result.</summary>
    public string? Message { get; set; }

    public IReadOnlyList<ProviderWriteOutcome> Outcomes {
        get {
            lock (_outcomes)
                return _outcomes.ToArray();
        }
    }

    public void Record(ProviderWriteOutcome outcome) {
        int sequence;
        lock (_outcomes) {
            _outcomes.Add(outcome);
            sequence = _outcomes.Count;
        }
        try {
            _store.AddAction(OperationId, sequence, outcome);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogWarning("Could not record an outcome for operation {OperationId}: {ErrorMessage}", OperationId, error.Message);
        }
    }

    public void ReportProgress(int done, int? total) {
        try {
            _store.SetProgress(OperationId, done, total);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogWarning("Could not record progress for operation {OperationId}: {ErrorMessage}", OperationId, error.Message);
        }
    }
}
