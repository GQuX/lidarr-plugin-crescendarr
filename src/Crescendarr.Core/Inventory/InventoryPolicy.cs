using System.Collections.Immutable;

namespace Crescendarr.Core.Inventory;

/// <summary>
/// Checkable stability and trust policy. A completed observation is
/// <see cref="InventoryItemState.CompletedStable"/> only when ALL hold:
/// <list type="number">
/// <item>the client reported Completed in at least <see cref="MinStablePolls"/> consecutive
///   successful polls of that client, with no failed poll, absence, clock regression or
///   poll gap larger than <see cref="MaxPollGap"/> in between;</item>
/// <item>output path, total bytes and remaining bytes were identical in each of those polls;</item>
/// <item>remaining bytes were zero or unreported, and total bytes were known when
///   <see cref="RequireKnownSize"/> is set;</item>
/// <item>at least <see cref="MinStableAge"/> elapsed on the observer clock between the first
///   and the latest of those polls.</item>
/// </list>
/// Evaluation additionally refuses stable items whose client is stale, unhealthy,
/// unsupported, or whose anchored root is no longer allowed.
/// </summary>
public sealed record InventoryPolicy(
    PathStyle PathStyle,
    ImmutableArray<string> AllowedOutputRoots,
    ImmutableArray<string> SupportedImplementations,
    int MinStablePolls,
    TimeSpan MinStableAge,
    TimeSpan MaxPollGap,
    TimeSpan StaleAfter,
    int MissingAfterPolls,
    int MaxItemsPerClient,
    bool RequireKnownSize)
{
    /// <summary>
    /// Conservative defaults assuming stock Lidarr's roughly one-minute download refresh.
    /// That interval is an assumption about the host scheduler, not a proven contract.
    /// </summary>
    public static InventoryPolicy Conservative(PathStyle style, IEnumerable<string> roots, IEnumerable<string> implementations) => new(
        style,
        roots.ToImmutableArray(),
        implementations.ToImmutableArray(),
        MinStablePolls: 3,
        MinStableAge: TimeSpan.FromMinutes(2),
        MaxPollGap: TimeSpan.FromMinutes(5),
        StaleAfter: TimeSpan.FromMinutes(15),
        MissingAfterPolls: 3,
        MaxItemsPerClient: 5000,
        RequireKnownSize: true);

    /// <summary>Returns every violation; an empty result means the policy is usable.</summary>
    public ImmutableArray<string> Validate()
    {
        var errors = ImmutableArray.CreateBuilder<string>();

        if (PathStyle is not (PathStyle.Posix or PathStyle.Windows)) errors.Add("PathStyle is not defined.");

        if (AllowedOutputRoots.IsDefaultOrEmpty)
        {
            errors.Add("At least one allowed output root is required.");
        }
        else
        {
            foreach (var root in AllowedOutputRoots)
            {
                var check = PathCanonicalizer.Canonicalize(root, PathStyle);
                if (check.Verdict != PathVerdict.Ok || !string.Equals(check.CanonicalPath, root, StringComparison.Ordinal))
                {
                    errors.Add($"Allowed root '{InventoryText.Evidence(root)}' is not in canonical form.");
                    continue;
                }

                if (root is "/" || (PathStyle == PathStyle.Windows && root.Length == 3))
                    errors.Add($"Allowed root '{root}' is a filesystem root and too broad.");
            }

            for (var i = 0; i < AllowedOutputRoots.Length; i++)
            {
                for (var j = i + 1; j < AllowedOutputRoots.Length; j++)
                {
                    var a = AllowedOutputRoots[i];
                    var b = AllowedOutputRoots[j];
                    if (a is null || b is null) continue;
                    if (PathCanonicalizer.PathsEqual(a, b, PathStyle))
                        errors.Add($"Allowed root '{a}' is listed twice.");
                    else if (PathCanonicalizer.IsStrictlyUnder(a, b, PathStyle) || PathCanonicalizer.IsStrictlyUnder(b, a, PathStyle))
                        errors.Add($"Allowed roots '{a}' and '{b}' overlap.");
                }
            }
        }

        if (SupportedImplementations.IsDefaultOrEmpty)
        {
            errors.Add("At least one supported client implementation is required.");
        }
        else
        {
            foreach (var implementation in SupportedImplementations)
            {
                if (!InventoryText.IsImplementationToken(implementation))
                    errors.Add($"Implementation '{InventoryText.Evidence(implementation)}' is not an ASCII class-name token.");
            }

            if (SupportedImplementations.Distinct(StringComparer.Ordinal).Count() != SupportedImplementations.Length)
                errors.Add("Supported implementations contain duplicates.");
        }

        if (MinStablePolls is < 2 or > 1000) errors.Add("MinStablePolls must be between 2 and 1000.");
        if (MinStableAge <= TimeSpan.Zero || MinStableAge > TimeSpan.FromDays(7)) errors.Add("MinStableAge must be positive and at most 7 days.");
        if (MaxPollGap <= TimeSpan.Zero) errors.Add("MaxPollGap must be positive.");
        if (StaleAfter < MaxPollGap) errors.Add("StaleAfter must be at least MaxPollGap.");
        if (MissingAfterPolls is < 2 or > 1000) errors.Add("MissingAfterPolls must be between 2 and 1000.");
        if (MaxItemsPerClient is < 1 or > 100_000) errors.Add("MaxItemsPerClient must be between 1 and 100000.");

        return errors.ToImmutable();
    }

    internal bool Supports(string implementation) =>
        !SupportedImplementations.IsDefaultOrEmpty && SupportedImplementations.Contains(implementation, StringComparer.Ordinal);

    internal bool RootAllowed(string root) =>
        !AllowedOutputRoots.IsDefaultOrEmpty && AllowedOutputRoots.Any(r => PathCanonicalizer.PathsEqual(r, root, PathStyle));
}
