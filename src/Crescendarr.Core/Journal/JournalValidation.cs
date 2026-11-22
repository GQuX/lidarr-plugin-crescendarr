using System.Collections.Immutable;
using Crescendarr.Core.Inventory;

namespace Crescendarr.Core.Journal;

/// <summary>Strict, bounded validation of an operation binding. Any error means the binding is unusable.</summary>
public static class JournalValidation
{
    public const int MaxTracks = 200;
    public const int MaxRelativePathLength = 1024;
    public const long MaxContentLength = 64L * 1024 * 1024 * 1024;

    public static ImmutableArray<string> Validate(OperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var errors = ImmutableArray.CreateBuilder<string>();
        var style = binding.PathStyle;

        if (!IsUuid(binding.OperationId)) errors.Add("OperationId must be a lowercase canonical UUID.");
        if (binding.AlbumId <= 0) errors.Add("AlbumId must be positive.");
        if (binding.MonitoredReleaseId <= 0) errors.Add("MonitoredReleaseId must be positive.");
        if (!IsUuid(binding.ReleaseGroupMbid)) errors.Add("ReleaseGroupMbid must be a lowercase canonical UUID.");
        if (!IsUuid(binding.MonitoredReleaseMbid)) errors.Add("MonitoredReleaseMbid must be a lowercase canonical UUID.");
        if (style is not (PathStyle.Posix or PathStyle.Windows)) errors.Add("PathStyle is not defined.");

        if (binding.Download is { } download)
        {
            if (download.ClientDefinitionId <= 0) errors.Add("Download client definition ID must be positive.");
            if (!InventoryText.IsValidDownloadId(download.DownloadId)) errors.Add("Download ID is not a clean token.");
            if (download.InventoryEvidenceDigest is not null && !IsSha256(download.InventoryEvidenceDigest)) errors.Add("Inventory evidence digest must be lowercase SHA-256 hex.");
        }

        var roots = new[] { ("LibraryRoot", binding.LibraryRoot), ("StagingRoot", binding.StagingRoot), ("OperationRoot", binding.OperationRoot) };
        foreach (var (name, root) in roots)
        {
            if (!IsCanonical(root, style)) errors.Add($"{name} is not a canonical absolute path.");
        }

        for (var i = 0; i < roots.Length; i++)
        {
            for (var j = i + 1; j < roots.Length; j++)
            {
                var (a, b) = (roots[i].Item2, roots[j].Item2);
                if (a is null || b is null) continue;
                if (PathCanonicalizer.PathsEqual(a, b, style) || PathCanonicalizer.IsStrictlyUnder(a, b, style) || PathCanonicalizer.IsStrictlyUnder(b, a, style))
                    errors.Add($"{roots[i].Item1} and {roots[j].Item1} overlap.");
            }
        }

        if (binding.OperationRoot is not null && binding.OperationId is not null &&
            !binding.OperationRoot.EndsWith(binding.OperationId, StringComparison.Ordinal))
            errors.Add("OperationRoot must end with the operation ID.");

        if (binding.Tracks.IsDefaultOrEmpty) errors.Add("At least one track is required.");
        else if (binding.Tracks.Length > MaxTracks) errors.Add($"At most {MaxTracks} tracks per operation.");
        else
        {
            if (binding.Tracks.Select(t => t.TrackId).Distinct().Count() != binding.Tracks.Length) errors.Add("Duplicate track IDs.");
            if (binding.Tracks.Select(t => t.OriginalTrackFileId).Distinct().Count() != binding.Tracks.Length) errors.Add("Duplicate original track-file IDs (multi-track files are not supported).");

            var allPaths = new List<string>();
            foreach (var track in binding.Tracks)
            {
                if (track is null) { errors.Add("Null track binding."); continue; }
                var label = $"Track {track.TrackId}";
                if (track.TrackId <= 0) errors.Add($"{label}: TrackId must be positive.");
                if (track.OriginalTrackFileId <= 0) errors.Add($"{label}: OriginalTrackFileId must be positive (only replacements are journaled).");
                if (!IsUuid(track.RecordingMbid)) errors.Add($"{label}: RecordingMbid must be a lowercase canonical UUID.");
                RequireUnder(errors, label, "OriginalPath", track.OriginalPath, binding.LibraryRoot, style);
                RequireUnder(errors, label, "DestinationPath", track.DestinationPath, binding.LibraryRoot, style);
                RequireUnder(errors, label, "IncomingPath", track.IncomingPath, binding.StagingRoot, style);
                RequireUnder(errors, label, "BackupAudioPath", track.BackupAudioPath, binding.OperationRoot, style);
                RequireContent(errors, label, "Original", track.Original);
                RequireContent(errors, label, "Incoming", track.Incoming);
                if (track.Original is not null && track.Incoming is not null && track.Original == track.Incoming)
                    errors.Add($"{label}: incoming content equals original content; nothing to replace.");
                allPaths.AddRange(new[] { track.OriginalPath, track.IncomingPath, track.BackupAudioPath });
                if (!PathEq(track.DestinationPath, track.OriginalPath, style)) allPaths.Add(track.DestinationPath);

                if (track.Lyric is { } lyric)
                {
                    if (lyric.LyricFileId <= 0) errors.Add($"{label}: LyricFileId must be positive.");
                    if (!IsSafeRelative(lyric.RelativePath, style)) errors.Add($"{label}: lyric relative path is not a safe relative path.");
                    RequireUnder(errors, label, "Lyric.Path", lyric.Path, binding.LibraryRoot, style);
                    RequireUnder(errors, label, "Lyric.BackupPath", lyric.BackupPath, binding.OperationRoot, style);
                    RequireContent(errors, label, "Lyric", lyric.Content);
                    allPaths.AddRange(new[] { lyric.Path, lyric.BackupPath });
                }
            }

            var nonNull = allPaths.Where(p => p is not null).ToList();
            // Distinct roles must never alias one another, compared case-insensitively on every
            // host because a NAS may fold case even under a case-sensitive path grammar.
            if (nonNull.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nonNull.Count) errors.Add("Two bound paths alias each other.");
        }

        return errors.ToImmutable();
    }

    public static bool IsUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonical(string? path, PathStyle style)
    {
        var check = PathCanonicalizer.Canonicalize(path, style);
        return check.Verdict == PathVerdict.Ok && string.Equals(check.CanonicalPath, path, StringComparison.Ordinal);
    }

    private static bool PathEq(string? a, string? b, PathStyle style) =>
        a is not null && b is not null && PathCanonicalizer.PathsEqual(a, b, style);

    private static void RequireUnder(ImmutableArray<string>.Builder errors, string label, string name, string? path, string? root, PathStyle style)
    {
        if (!IsCanonical(path, style)) { errors.Add($"{label}: {name} is not a canonical absolute path."); return; }
        if (root is null || !PathCanonicalizer.IsStrictlyUnder(path!, root, style)) errors.Add($"{label}: {name} is not under its root.");
    }

    private static void RequireContent(ImmutableArray<string>.Builder errors, string label, string name, ContentId? content)
    {
        if (content is null) { errors.Add($"{label}: {name} content identity missing."); return; }
        if (!IsSha256(content.Sha256)) errors.Add($"{label}: {name} SHA-256 must be lowercase hex.");
        if (content.Length is <= 0 or > MaxContentLength) errors.Add($"{label}: {name} length out of range.");
    }

    private static bool IsSafeRelative(string? path, PathStyle style)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxRelativePathLength) return false;
        if (InventoryText.UnsafeCharacter(path) is not null) return false;
        if (path[0] == '/' || path[0] == '\\' || path.Contains(':', StringComparison.Ordinal)) return false;
        if (style == PathStyle.Posix && path.Contains('\\', StringComparison.Ordinal)) return false;
        // Windows accepts both separators, so both must be treated as separators when looking for "..".
        var segments = style == PathStyle.Windows ? path.Split('\\', '/') : path.Split('/');
        return segments.All(s => s.Length > 0 && s != "." && s != ".." && s.Trim() == s);
    }
}
