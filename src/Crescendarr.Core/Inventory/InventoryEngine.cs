using System.Collections.Immutable;

namespace Crescendarr.Core.Inventory;

/// <summary>
/// Pure transition function: (state, policy, poll) -> state. Deterministic, total over
/// valid inputs, and idempotent under replay (a poll whose sequence is not greater than
/// the client's last processed sequence returns the input state unchanged).
/// </summary>
public static class InventoryEngine
{
    private static readonly long MinPlausibleTicks = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static readonly long MaxPlausibleTicks = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public static InventoryState Apply(InventoryState state, InventoryPolicy policy, ClientPoll poll)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(poll.Client);

        var errors = policy.Validate();
        if (!errors.IsEmpty)
            throw new ArgumentException("Invalid inventory policy: " + string.Join(" ", errors), nameof(policy));
        if (poll.Client.DefinitionId <= 0 || !InventoryText.IsImplementationToken(poll.Client.Implementation) || poll.Sequence < 0)
            throw new ArgumentException("Poll lacks a valid provider identity or sequence.", nameof(poll));
        if (!Enum.IsDefined(poll.Outcome))
            throw new ArgumentException("Poll outcome is not defined.", nameof(poll));

        var id = poll.Client.DefinitionId;
        state.Clients.TryGetValue(id, out var existingClient);

        // Ordering is owned by the sequence, never the clock. A non-increasing sequence is
        // either a replay / late delivery (not newer than the last poll: ignored, identical
        // state) or an adapter that lost its persisted counter (newer timestamp). The latter
        // is flagged sticky and blocks readiness at once instead of waiting for StaleAfter.
        if (existingClient is not null && poll.Sequence <= existingClient.LastSequence)
        {
            var lostCounter = existingClient.LastPollTicks is { } previous && poll.ObservedAt.UtcTicks > previous;
            if (!lostCounter || existingClient.StickyReasons.Contains(InventoryReason.SequenceReset)) return state;

            var flagged = existingClient with { StickyReasons = existingClient.StickyReasons.Add(InventoryReason.SequenceReset) };
            var held = MapClientItems(state.Items, id, r => ResetStability(r, InventoryReason.StabilityResetSequenceReset));
            return Finish(state, flagged, held, policy);
        }

        var client = existingClient ?? ClientRecord.New(poll.Client);
        var items = state.Items;
        var pollReasons = ImmutableSortedSet.CreateBuilder<InventoryReason>();

        if (!string.Equals(client.Implementation, poll.Client.Implementation, StringComparison.Ordinal))
        {
            // Same definition ID, different implementation: the provider row was replaced or
            // the adapter is confused. Keep the original identity and all evidence; ingest nothing.
            pollReasons.Add(InventoryReason.ClientIdentityConflict);
            client = client with
            {
                LastSequence = poll.Sequence,
                StickyReasons = client.StickyReasons.Add(InventoryReason.ClientIdentityConflict),
                LastPollReasons = pollReasons.ToImmutable(),
            };
            items = MapClientItems(items, id, r => Escalate(r, InventoryItemState.RequiresReview, InventoryReason.ClientIdentityChanged));
            return Finish(state, client, items, policy);
        }

        if (InventoryText.IsCleanToken(poll.Client.Name, 128)) client = client with { Name = poll.Client.Name };

        var ticks = poll.ObservedAt.UtcTicks;
        if (ticks < MinPlausibleTicks || ticks > MaxPlausibleTicks)
        {
            pollReasons.Add(InventoryReason.InvalidTimestamp);
            client = client with { LastSequence = poll.Sequence, LastPollReasons = pollReasons.ToImmutable() };
            items = MapClientItems(items, id, r => ResetStability(r, InventoryReason.StabilityResetInvalidTimestamp));
            return Finish(state, client, items, policy);
        }

        InventoryReason? stabilityReset = null;
        if (client.LastPollTicks is { } lastPoll)
        {
            if (ticks < lastPoll)
            {
                pollReasons.Add(InventoryReason.ClockRegressed);
                stabilityReset = InventoryReason.StabilityResetClockRegression;
            }
            else if (ticks - lastPoll > policy.MaxPollGap.Ticks)
            {
                pollReasons.Add(InventoryReason.PollGapExceeded);
                stabilityReset = InventoryReason.StabilityResetPollGap;
            }
        }

        client = client with { LastSequence = poll.Sequence, LastPollTicks = ticks };

        if (!policy.Supports(poll.Client.Implementation))
        {
            pollReasons.Add(InventoryReason.ClientUnsupported);
            client = client with { LastPollReasons = pollReasons.ToImmutable() };
            items = MapClientItems(items, id, r => Escalate(r, InventoryItemState.RequiresReview, InventoryReason.ClientNoLongerSupported));
            return Finish(state, client, items, policy);
        }

        if (poll.HostCompletedDownloadHandlingEnabled)
        {
            // Stock CDH owns completed items while it is on. Do not compete: ingest nothing,
            // and treat anything already completed as possibly processed by the stock importer.
            pollReasons.Add(InventoryReason.StockHandlingActive);
            client = client with { LastPollReasons = pollReasons.ToImmutable() };
            items = MapClientItems(items, id, r => r.EverCompleted || r.LastStatus == ClientItemStatus.Completed
                ? Escalate(r, InventoryItemState.RequiresReview, InventoryReason.StockHandlingWasActive)
                : ResetStability(r, InventoryReason.StabilityResetClientUnavailable));
            return Finish(state, client, items, policy);
        }

        // A "successful" poll without an items array is missing client data, not an empty
        // client. Treating it as empty would age every item toward MissingFromClient.
        var itemsMissing = poll.Outcome == PollOutcome.Succeeded && poll.Items.IsDefault;
        if (poll.Outcome == PollOutcome.ClientUnavailable || itemsMissing)
        {
            // Temporary GetItems failure: absence is meaningless, so nothing becomes missing;
            // stability must be re-proven after recovery. All evidence is retained.
            pollReasons.Add(InventoryReason.ClientPollFailed);
            client = client with
            {
                ConsecutiveFailures = client.ConsecutiveFailures + 1,
                LastFailureReason = itemsMissing ? "succeeded-without-items" : InventoryText.Evidence(poll.FailureReason) ?? "unspecified",
                LastPollReasons = pollReasons.ToImmutable(),
            };
            items = MapClientItems(items, id, r => ResetStability(r, InventoryReason.StabilityResetClientUnavailable));
            return Finish(state, client, items, policy);
        }

        // Successful poll.
        var snapshots = poll.Items;
        var unkeyable = snapshots.Count(s => s is null || !InventoryText.IsValidDownloadId(s.DownloadId));
        if (unkeyable > 0) pollReasons.Add(InventoryReason.UnkeyableItemsReported);

        var groups = snapshots
            .Where(s => s is not null && InventoryText.IsValidDownloadId(s.DownloadId))
            .GroupBy(s => s.DownloadId!, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Distinct: g.Distinct().OrderBy(SnapshotSortKey, StringComparer.Ordinal).ToList()))
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .ToList();

        var knownIds = items.Keys.Where(k => k.ClientDefinitionId == id).Select(k => k.DownloadId).ToHashSet(StringComparer.Ordinal);
        var capacityLeft = policy.MaxItemsPerClient - knownIds.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = items.ToBuilder();

        foreach (var (downloadId, distinct) in groups)
        {
            var key = new ItemKey(id, downloadId);
            builder.TryGetValue(key, out var existing);
            if (existing is null)
            {
                if (capacityLeft <= 0)
                {
                    pollReasons.Add(InventoryReason.CapacityExceeded);
                    continue;
                }

                capacityLeft--;
            }

            seen.Add(downloadId);
            var record = Observe(key, existing, distinct[0], policy, ticks, poll.Sequence, stabilityReset);
            if (distinct.Count > 1)
            {
                // Every conflicting variant is judged; the worst evidence wins, so a poisoned
                // variant cannot hide behind a clean one and be reported as merely Ambiguous.
                var variantPaths = record.VariantPaths.ToBuilder();
                foreach (var variant in distinct)
                {
                    var check = PathCanonicalizer.CheckOutput(variant.OutputPath, policy.PathStyle, policy.AllowedOutputRoots);
                    if (check.CanonicalPath is not null) variantPaths.Add(check.CanonicalPath);
                }

                // Every claimed path joins global collision detection, so a discarded variant
                // cannot silently point at another item's output.
                record = record with { VariantPaths = variantPaths.ToImmutable() };

                foreach (var variant in distinct.Skip(1))
                {
                    var alternative = Observe(key, existing, variant, policy, ticks, poll.Sequence, stabilityReset);
                    if (alternative.State != InventoryItemState.RequiresReview) continue;
                    record = record with
                    {
                        State = InventoryItemState.RequiresReview,
                        Reasons = record.Reasons.Union(alternative.Reasons),
                        RawPathEvidence = alternative.RawPathEvidence ?? record.RawPathEvidence,
                        StablePollCount = 0,
                        StableSinceTicks = null,
                    };
                }

                record = Escalate(record, InventoryItemState.Ambiguous, InventoryReason.DuplicateIdInPoll);
            }
            builder[key] = record;
        }

        foreach (var key in builder.Keys.Where(k => k.ClientDefinitionId == id && !seen.Contains(k.DownloadId)).ToList())
        {
            builder[key] = Absent(builder[key], policy);
        }

        client = client with
        {
            ConsecutiveFailures = 0,
            LastSuccessTicks = ticks,
            UnkeyableItemsTotal = client.UnkeyableItemsTotal + unkeyable,
            LastPollReasons = pollReasons.ToImmutable(),
        };

        return Finish(state, client, builder.ToImmutable(), policy);
    }

    private static ItemRecord Observe(
        ItemKey key,
        ItemRecord? existing,
        ClientItemSnapshot snapshot,
        InventoryPolicy policy,
        long ticks,
        long sequence,
        InventoryReason? stabilityReset)
    {
        var isNew = existing is null;
        var record = existing ?? new ItemRecord(
            key, InventoryItemState.Observed, ImmutableSortedSet<InventoryReason>.Empty,
            FirstSeenTicks: ticks, LastSeenTicks: ticks, LastSeenSequence: sequence,
            LastStatus: ClientItemStatus.Unknown, Title: null, Category: null,
            CurrentPath: null, CurrentRoot: null, AnchoredPath: null, AnchoredRoot: null, RawPathEvidence: null,
            TotalBytes: null, RemainingBytes: null, EverCompleted: false,
            StablePollCount: 0, StableSinceTicks: null, ConsecutiveAbsences: 0, VariantPaths: ItemRecord.NoPaths);

        var reappeared = record.State == InventoryItemState.MissingFromClient;
        var escalations = new List<InventoryReason>();
        var notes = ImmutableSortedSet.CreateBuilder<InventoryReason>();

        // Without immutable job provenance, a job that returns after MissingFromClient may be
        // a different download reusing both ID and path (client retention, restart). Always
        // hold it for review. After a shorter absence, changed title or size is equally suspect.
        if (reappeared) escalations.Add(InventoryReason.ReappearedAfterMissing);
        else if (record.ConsecutiveAbsences > 0 && record.EverCompleted &&
                 (!string.Equals(record.Title, snapshot.Title, StringComparison.Ordinal) || record.TotalBytes != snapshot.TotalBytes))
            escalations.Add(InventoryReason.ReappearedWithChangedEvidence);

        var metadataClean = InventoryText.IsCleanOptional(snapshot.Title) && InventoryText.IsCleanOptional(snapshot.Category);
        if (!metadataClean) escalations.Add(InventoryReason.UntrustedMetadata);

        var status = Enum.IsDefined(snapshot.Status) ? snapshot.Status : ClientItemStatus.Unknown;
        if (status == ClientItemStatus.Unknown) escalations.Add(InventoryReason.UnsupportedStatus);
        if (status == ClientItemStatus.Failed) escalations.Add(InventoryReason.ClientReportedFailure);
        if (record.EverCompleted && status != ClientItemStatus.Completed) escalations.Add(InventoryReason.StatusRegressedAfterCompletion);

        var path = PathCanonicalizer.CheckOutput(snapshot.OutputPath, policy.PathStyle, policy.AllowedOutputRoots);
        var rawEvidence = record.RawPathEvidence;
        string? canonical = null, root = null;
        switch (path.Verdict)
        {
            case PathVerdict.Unsafe:
                escalations.Add(InventoryReason.OutputPathUnsafe);
                rawEvidence = InventoryText.Evidence(snapshot.OutputPath);
                break;
            case PathVerdict.OutsideAllowedRoots:
                escalations.Add(InventoryReason.OutputPathOutsideAllowedRoots);
                rawEvidence = InventoryText.Evidence(snapshot.OutputPath);
                break;
            case PathVerdict.Missing:
                if (status == ClientItemStatus.Completed) escalations.Add(InventoryReason.OutputPathMissing);
                break;
            case PathVerdict.Ok:
                canonical = path.CanonicalPath;
                root = path.MatchedRoot;
                break;
        }

        if (canonical is not null && record.AnchoredPath is not null && !string.Equals(canonical, record.AnchoredPath, StringComparison.Ordinal))
        {
            rawEvidence = InventoryText.Evidence(snapshot.OutputPath);
            if (reappeared) escalations.Add(InventoryReason.IdReusedWithDifferentPath);
            else if (string.Equals(canonical, record.AnchoredPath, StringComparison.OrdinalIgnoreCase)) escalations.Add(InventoryReason.OutputPathCasingChanged);
            else escalations.Add(InventoryReason.OutputPathChangedAfterCompletion);
            if (!string.Equals(root, record.AnchoredRoot, StringComparison.Ordinal)) escalations.Add(InventoryReason.OutputRootChanged);
        }

        if (snapshot.TotalBytes is < 0 || snapshot.RemainingBytes is < 0 ||
            (snapshot.TotalBytes is { } total && snapshot.RemainingBytes is { } remaining && remaining > total))
            escalations.Add(InventoryReason.SizeInvalid);

        var updated = record with
        {
            LastSeenTicks = ticks,
            LastSeenSequence = sequence,
            LastStatus = status,
            Title = metadataClean ? snapshot.Title : record.Title,
            Category = metadataClean ? snapshot.Category : record.Category,
            CurrentPath = canonical,
            CurrentRoot = root,
            RawPathEvidence = rawEvidence,
            TotalBytes = snapshot.TotalBytes,
            RemainingBytes = snapshot.RemainingBytes,
            ConsecutiveAbsences = 0,
        };

        if (escalations.Count > 0 || record.IsSticky)
        {
            var reasons = record.IsSticky ? record.Reasons.ToBuilder() : ImmutableSortedSet.CreateBuilder<InventoryReason>();
            reasons.UnionWith(escalations);
            reasons.UnionWith(notes);
            return updated with
            {
                State = escalations.Count > 0 ? InventoryItemState.RequiresReview : record.State,
                Reasons = reasons.ToImmutable(),
                EverCompleted = record.EverCompleted || status == ClientItemStatus.Completed,
                StablePollCount = 0,
                StableSinceTicks = null,
            };
        }

        if (status != ClientItemStatus.Completed)
        {
            return updated with
            {
                State = isNew ? InventoryItemState.Observed : InventoryItemState.StillDownloading,
                Reasons = notes.ToImmutable(),
                StablePollCount = 0,
                StableSinceTicks = null,
            };
        }

        // Completed, trusted, and not escalated. Anchor the output path on first completion.
        var provable = true;
        // Completion must be proven by an explicit zero; stock RemainingSize is non-nullable,
        // so an adapter that cannot supply it must report null and the item never stabilizes.
        if (snapshot.RemainingBytes is null) { notes.Add(InventoryReason.RemainingBytesUnknown); provable = false; }
        else if (snapshot.RemainingBytes > 0) { notes.Add(InventoryReason.CompletedWithRemainingBytes); provable = false; }
        // Stock DownloadClientItem.TotalSize is a non-nullable long, so clients that do not
        // know a size report 0. Zero is therefore treated exactly like an unknown size.
        if (policy.RequireKnownSize && snapshot.TotalBytes is null or 0) { notes.Add(InventoryReason.SizeUnknown); provable = false; }
        if (stabilityReset is { } reset) notes.Add(reset);

        var wasCompletedLastPoll = record.EverCompleted && record.LastStatus == ClientItemStatus.Completed;
        var sizeChanged = wasCompletedLastPoll &&
            (record.TotalBytes != snapshot.TotalBytes || record.RemainingBytes != snapshot.RemainingBytes);
        if (sizeChanged) notes.Add(InventoryReason.SizeChangedWhileCompleted);

        var continues = wasCompletedLastPoll && !reappeared && stabilityReset is null && !sizeChanged &&
            record.StablePollCount > 0 && record.StableSinceTicks is not null &&
            string.Equals(record.CurrentPath, canonical, StringComparison.Ordinal);

        int count;
        long? since;
        if (!provable) { count = 0; since = null; }
        else if (continues) { count = record.StablePollCount + 1; since = record.StableSinceTicks; }
        else { count = 1; since = ticks; }

        var stable = !isNew && provable && count >= policy.MinStablePolls && since is { } start && ticks - start >= policy.MinStableAge.Ticks;

        return updated with
        {
            State = isNew ? InventoryItemState.Observed : stable ? InventoryItemState.CompletedStable : InventoryItemState.CompletedSettling,
            Reasons = notes.ToImmutable(),
            EverCompleted = true,
            AnchoredPath = record.AnchoredPath ?? canonical,
            AnchoredRoot = record.AnchoredRoot ?? root,
            StablePollCount = count,
            StableSinceTicks = since,
        };
    }

    private static ItemRecord Absent(ItemRecord record, InventoryPolicy policy)
    {
        var absences = record.ConsecutiveAbsences + 1;
        if (record.IsSticky)
        {
            var reasons = absences >= policy.MissingAfterPolls ? record.Reasons.Add(InventoryReason.AbsentFromClient) : record.Reasons;
            return record with { ConsecutiveAbsences = absences, Reasons = reasons, StablePollCount = 0, StableSinceTicks = null };
        }

        if (record.State == InventoryItemState.MissingFromClient)
            return record with { ConsecutiveAbsences = absences };

        if (absences >= policy.MissingAfterPolls)
        {
            return record with
            {
                State = InventoryItemState.MissingFromClient,
                Reasons = ImmutableSortedSet.Create(InventoryReason.AbsentFromClient),
                ConsecutiveAbsences = absences,
                StablePollCount = 0,
                StableSinceTicks = null,
            };
        }

        return record with
        {
            State = record.State == InventoryItemState.CompletedStable ? InventoryItemState.CompletedSettling : record.State,
            Reasons = ImmutableSortedSet.Create(InventoryReason.NotSeenInLatestPoll),
            ConsecutiveAbsences = absences,
            StablePollCount = 0,
            StableSinceTicks = null,
        };
    }

    private static ItemRecord ResetStability(ItemRecord record, InventoryReason reason)
    {
        if (record.IsSticky || record.State == InventoryItemState.MissingFromClient)
            return record with { StablePollCount = 0, StableSinceTicks = null };

        return record with
        {
            State = record.State == InventoryItemState.CompletedStable ? InventoryItemState.CompletedSettling : record.State,
            Reasons = record.Reasons.Add(reason),
            StablePollCount = 0,
            StableSinceTicks = null,
        };
    }

    internal static ItemRecord Escalate(ItemRecord record, InventoryItemState target, InventoryReason reason)
    {
        var state = record.State == InventoryItemState.RequiresReview || target == InventoryItemState.RequiresReview
            ? InventoryItemState.RequiresReview
            : InventoryItemState.Ambiguous;
        var reasons = record.IsSticky ? record.Reasons.Add(reason) : ImmutableSortedSet.Create(reason);
        return record with { State = state, Reasons = reasons, StablePollCount = 0, StableSinceTicks = null };
    }

    private static ImmutableSortedDictionary<ItemKey, ItemRecord> MapClientItems(
        ImmutableSortedDictionary<ItemKey, ItemRecord> items, int clientId, Func<ItemRecord, ItemRecord> map)
    {
        var builder = items.ToBuilder();
        foreach (var key in items.Keys.Where(k => k.ClientDefinitionId == clientId))
        {
            builder[key] = map(items[key]);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Global, order-independent identity checks recomputed after every poll, so the
    /// result does not depend on which client was polled first.
    /// </summary>
    private static InventoryState Finish(InventoryState state, ClientRecord client, ImmutableSortedDictionary<ItemKey, ItemRecord> items, InventoryPolicy policy)
    {
        var builder = items.ToBuilder();

        foreach (var group in items.Values.GroupBy(r => r.Key.DownloadId, StringComparer.Ordinal))
        {
            if (group.Select(r => r.Key.ClientDefinitionId).Distinct().Count() < 2) continue;
            foreach (var record in group)
                builder[record.Key] = Escalate(builder[record.Key], InventoryItemState.Ambiguous, InventoryReason.DuplicateIdAcrossClients);
        }

        // Collision is checked case-insensitively on every host: an SMB/NAS backing store
        // may fold case even when the Lidarr host path grammar is case-sensitive. Two items
        // collide when their output paths are equal or one contains the other.
        var separator = policy.PathStyle == PathStyle.Windows ? '\\' : '/';
        // Every path a record has claimed participates: anchored, current, and conflicting
        // variants. A record never collides with itself.
        var owners = new Dictionary<string, SortedSet<ItemKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in items.Values)
        {
            foreach (var path in record.ClaimedPaths())
            {
                if (!owners.TryGetValue(path, out var set)) owners[path] = set = new SortedSet<ItemKey>();
                set.Add(record.Key);
            }
        }

        var colliding = new SortedSet<ItemKey>();
        foreach (var (path, keys) in owners)
        {
            if (keys.Count > 1) colliding.UnionWith(keys);
            for (var cut = path.LastIndexOf(separator); cut > 0; cut = path.LastIndexOf(separator, cut - 1))
            {
                if (!owners.TryGetValue(path[..cut], out var ancestors)) continue;
                var involved = new SortedSet<ItemKey>(keys);
                involved.UnionWith(ancestors);
                if (involved.Count > 1) colliding.UnionWith(involved);
            }
        }

        foreach (var key in colliding)
            builder[key] = Escalate(builder[key], InventoryItemState.Ambiguous, InventoryReason.OutputPathCollision);

        return new InventoryState(state.Clients.SetItem(client.DefinitionId, client), builder.ToImmutable());
    }

    private static string SnapshotSortKey(ClientItemSnapshot s) => string.Join(
        '\u001F',
        ((int)s.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.OutputPath ?? string.Empty,
        s.TotalBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        s.RemainingBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        s.Title ?? string.Empty,
        s.Category ?? string.Empty);
}
