using System.Collections.Immutable;

namespace Crescendarr.Core.Inventory;

/// <summary>
/// What a future executor may do next. <see cref="ReadyForPlanning"/> only permits
/// computing a read-only merge plan after host revalidation; it never means imported,
/// and nothing here ever permits clearing staging or removing a download.
/// </summary>
public enum PlanningEligibility
{
    ReadyForPlanning = 1,
    Wait = 2,
    OperatorReview = 3,

    /// <summary>Absent from the client. Retained as evidence; no automatic action of any kind.</summary>
    NoAction = 4,
}

public enum ClientHealthStatus
{
    Healthy = 1,
    NeverSucceeded = 2,
    Unavailable = 3,
    Stale = 4,
    ClockAhead = 5,
    Unsupported = 6,
    IdentityConflict = 7,
    StockHandlingActive = 8,
    Degraded = 9,

    /// <summary>The adapter lost its persisted poll sequence. Sticky; blocks readiness at once.</summary>
    SequenceReset = 10,
}

public sealed record ClientHealth(
    int DefinitionId,
    string Implementation,
    ClientHealthStatus Status,
    int ConsecutiveFailures,
    long? LastSuccessTicks,
    ImmutableArray<InventoryReason> Reasons);

public sealed record ItemDecision(
    ItemKey Key,
    InventoryItemState State,
    PlanningEligibility Eligibility,
    ImmutableArray<InventoryReason> Reasons,
    string EvidenceDigest);

public sealed record InventoryReport(
    ImmutableArray<ClientHealth> Clients,
    ImmutableArray<ItemDecision> Items)
{
    public int ReadyCount => Items.Count(i => i.Eligibility == PlanningEligibility.ReadyForPlanning);
    public int ReviewCount => Items.Count(i => i.Eligibility == PlanningEligibility.OperatorReview);
    public int MissingCount => Items.Count(i => i.State == InventoryItemState.MissingFromClient);
    public bool AnyClientUnhealthy => Clients.Any(c => c.Status != ClientHealthStatus.Healthy);
}

/// <summary>Pure read-side projection: (state, policy, now) -> decisions and health signals.</summary>
public static class InventoryEvaluator
{
    public static InventoryReport Evaluate(InventoryState state, InventoryPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = policy.Validate();
        if (!errors.IsEmpty)
            throw new ArgumentException("Invalid inventory policy: " + string.Join(" ", errors), nameof(policy));

        var nowTicks = now.UtcTicks;
        var health = state.Clients.Values.Select(c => Health(c, policy, nowTicks)).ToImmutableArray();
        var byId = health.ToDictionary(h => h.DefinitionId);

        var decisions = state.Items.Values.Select(item =>
        {
            byId.TryGetValue(item.Key.ClientDefinitionId, out var client);
            return Decide(item, client, policy);
        }).ToImmutableArray();

        return new InventoryReport(health, decisions);
    }

    private static ItemDecision Decide(ItemRecord item, ClientHealth? client, InventoryPolicy policy)
    {
        var reasons = item.Reasons.ToBuilder();
        PlanningEligibility eligibility;

        switch (item.State)
        {
            case InventoryItemState.RequiresReview:
            case InventoryItemState.Ambiguous:
                eligibility = PlanningEligibility.OperatorReview;
                break;
            case InventoryItemState.MissingFromClient:
                eligibility = PlanningEligibility.NoAction;
                break;
            case InventoryItemState.CompletedStable:
                if (item.AnchoredRoot is null || !policy.RootAllowed(item.AnchoredRoot) || item.AnchoredPath is null ||
                    !PathCanonicalizer.IsStrictlyUnder(item.AnchoredPath, item.AnchoredRoot, policy.PathStyle))
                {
                    reasons.Add(InventoryReason.OutputRootNoLongerAllowed);
                    eligibility = PlanningEligibility.OperatorReview;
                }
                else if (client is null || client.Status != ClientHealthStatus.Healthy)
                {
                    reasons.Add(client?.Status switch
                    {
                        ClientHealthStatus.Stale => InventoryReason.ClientStale,
                        ClientHealthStatus.Unavailable => InventoryReason.ClientUnavailable,
                        ClientHealthStatus.ClockAhead => InventoryReason.ClientClockAhead,
                        _ => InventoryReason.ClientNotHealthy,
                    });
                    eligibility = PlanningEligibility.Wait;
                }
                else
                {
                    eligibility = PlanningEligibility.ReadyForPlanning;
                }

                break;
            default:
                eligibility = PlanningEligibility.Wait;
                break;
        }

        return new ItemDecision(item.Key, item.State, eligibility, reasons.ToImmutableArray(), InventoryCodec.EvidenceDigest(item));
    }

    private static ClientHealth Health(ClientRecord client, InventoryPolicy policy, long nowTicks)
    {
        var reasons = client.StickyReasons.Union(client.LastPollReasons);
        ClientHealthStatus status;

        if (client.StickyReasons.Contains(InventoryReason.ClientIdentityConflict)) status = ClientHealthStatus.IdentityConflict;
        else if (client.StickyReasons.Contains(InventoryReason.SequenceReset)) status = ClientHealthStatus.SequenceReset;
        else if (!policy.Supports(client.Implementation) || client.LastPollReasons.Contains(InventoryReason.ClientUnsupported)) status = ClientHealthStatus.Unsupported;
        else if (client.LastPollReasons.Contains(InventoryReason.StockHandlingActive)) status = ClientHealthStatus.StockHandlingActive;
        else if (client.ConsecutiveFailures > 0) status = ClientHealthStatus.Unavailable;
        else if (client.LastSuccessTicks is not { } success) status = ClientHealthStatus.NeverSucceeded;
        else if (nowTicks < success) status = ClientHealthStatus.ClockAhead;
        else if (nowTicks - success > policy.StaleAfter.Ticks) status = ClientHealthStatus.Stale;
        else if (!client.LastPollReasons.IsEmpty) status = ClientHealthStatus.Degraded;
        else status = ClientHealthStatus.Healthy;

        return new ClientHealth(client.DefinitionId, client.Implementation, status, client.ConsecutiveFailures, client.LastSuccessTicks, reasons.ToImmutableArray());
    }
}
