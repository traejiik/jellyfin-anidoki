using System;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Preview;

/// <summary>
/// Works out what a Tracker → Jellyfin manual sync would mark as played, and marks nothing. It runs the same plan stage
/// as <see cref="Sync.SyncFromProvider"/> (so the same tracker list, ID lookups, anime list and ignore tags) and stores one
/// row per Jellyfin season, plus one per tracker entry that maps to none. Runs inside an operation.
/// </summary>
public sealed class InboundPreview(Sync sync, PreviewStore rows, IUserManager users, IUserDataManager userData, ApiName provider, Guid userId) {
    public async Task Run(OperationContext operation) {
        InboundPlanResult? plan;
        try {
            plan = await sync.Plan(userId, operation);
        } catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) {
            operation.Message = "Cancelled. Nothing was changed, and this preview can no longer be applied.";
            throw;
        }
        if (plan == null) {
            operation.Result = OperationState.NoChange;
            operation.Message = "The tracker returned no anime for the selected statuses. Check that the account is linked. Nothing was changed.";
            return;
        }

        bool applySavedMatches = Plugin.Instance?.PluginConfiguration.applySavedMatches ?? false;
        // Plans exist only when the user does, so the user is found whenever there is a season row to build.
        User? user = users.GetUserById(userId);
        int sequence = 0;
        int changes = 0;
        foreach (InboundSeasonPlan season in plan.Seasons) {
            PreviewRow row = InboundRows.FromSeason(season, provider, user!, userData, applySavedMatches);
            rows.Add(operation.OperationId, row with { Seq = sequence++ });
            if (row.Kind == PreviewRowKind.Change)
                changes++;
        }
        foreach (InboundUnresolved unresolved in plan.Unresolved)
            rows.Add(operation.OperationId, InboundRows.FromUnresolved(unresolved, provider, applySavedMatches) with { Seq = sequence++ });

        operation.Result = OperationState.Succeeded;
        string entries = plan.EntryCount == 1 ? "1 tracker entry" : $"{plan.EntryCount} tracker entries";
        string seasons = changes == 1 ? "1 season" : $"{changes} seasons";
        operation.Message = $"Previewed {entries}: {seasons} to mark as played. Nothing was changed.";
    }
}
