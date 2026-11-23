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

        if (!IsCanonical(binding.ArtistRoot, style)) errors.Add("ArtistRoot is not a canonical absolute path.");
        else if (binding.LibraryRoot is null || !PathCanonicalizer.IsStrictlyUnder(binding.ArtistRoot, binding.LibraryRoot, style)) errors.Add("ArtistRoot must be strictly under LibraryRoot.");

        var tracks = binding.Tracks.IsDefault ? ImmutableArray<TrackBinding>.Empty : binding.Tracks;
        var additions = binding.Additions.IsDefault ? ImmutableArray<AdditionBinding>.Empty : binding.Additions;
        var kept = binding.Kept.IsDefault ? ImmutableArray<KeptTrackBinding>.Empty : binding.Kept;
        var albumLyrics = binding.AlbumLyrics.IsDefault ? ImmutableArray<AlbumLyricBinding>.Empty : binding.AlbumLyrics;
        if (tracks.IsEmpty && additions.IsEmpty) errors.Add("At least one track is required (a replacement or an addition).");
        else if (tracks.Length + additions.Length + kept.Length > MaxTracks) errors.Add($"At most {MaxTracks} tracks per operation.");
        else
        {
            if (tracks.Select(t => t.TrackId).Distinct().Count() != tracks.Length) errors.Add("Duplicate track IDs.");
            if (tracks.Select(t => t.OriginalTrackFileId).Distinct().Count() != tracks.Length) errors.Add("Duplicate original track-file IDs (multi-track files are not supported).");

            var allPaths = new List<string>();
            foreach (var track in tracks)
            {
                if (track is null) { errors.Add("Null track binding."); continue; }
                var label = $"Track {track.TrackId}";
                if (track.TrackId <= 0) errors.Add($"{label}: TrackId must be positive.");
                if (track.OriginalTrackFileId <= 0) errors.Add($"{label}: OriginalTrackFileId must be positive (only replacements are journaled).");
                if (!IsUuid(track.RecordingMbid)) errors.Add($"{label}: RecordingMbid must be a lowercase canonical UUID.");
                RequireUnder(errors, label, "OriginalPath", track.OriginalPath, binding.ArtistRoot, style);
                RequireUnder(errors, label, "DestinationPath", track.DestinationPath, binding.ArtistRoot, style);
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
                    RequireUnder(errors, label, "Lyric.Path", lyric.Path, binding.ArtistRoot, style);
                    // Physical identity: the backed-up file must be exactly the file the host's registered
                    // relative path names, or the journal would protect a different file.
                    if (IsSafeRelative(lyric.RelativePath, style) && binding.ArtistRoot is not null &&
                        !string.Equals(JoinUnder(binding.ArtistRoot, lyric.RelativePath, style), lyric.Path, StringComparison.Ordinal))
                        errors.Add($"{label}: Lyric.Path is not ArtistRoot joined with the registered relative path.");
                    RequireUnder(errors, label, "Lyric.BackupPath", lyric.BackupPath, binding.OperationRoot, style);
                    RequireContent(errors, label, "Lyric", lyric.Content);
                    allPaths.AddRange(new[] { lyric.Path, lyric.BackupPath });
                }
            }

            // M18: additions, protected (kept) tracks and album-wide lyrics share the same operation and path rules.
            foreach (var a in additions)
            {
                if (a is null) { errors.Add("Null addition binding."); continue; }
                var label = $"Addition {a.TrackId}";
                if (a.TrackId <= 0) errors.Add($"{label}: TrackId must be positive.");
                if (!IsUuid(a.RecordingMbid)) errors.Add($"{label}: RecordingMbid must be a lowercase canonical UUID.");
                RequireUnder(errors, label, "DestinationPath", a.DestinationPath, binding.ArtistRoot, style);
                RequireUnder(errors, label, "IncomingPath", a.IncomingPath, binding.StagingRoot, style);
                RequireContent(errors, label, "Incoming", a.Incoming);
                allPaths.AddRange(new[] { a.DestinationPath, a.IncomingPath });
            }

            foreach (var k in kept)
            {
                if (k is null) { errors.Add("Null kept-track binding."); continue; }
                var label = $"Kept track {k.TrackId}";
                if (k.TrackId <= 0) errors.Add($"{label}: TrackId must be positive.");
                if (k.TrackFileId <= 0) errors.Add($"{label}: TrackFileId must be positive.");
                if (!IsUuid(k.RecordingMbid)) errors.Add($"{label}: RecordingMbid must be a lowercase canonical UUID.");
                RequireUnder(errors, label, "Path", k.Path, binding.ArtistRoot, style);
                RequireContent(errors, label, "Content", k.Content);
                allPaths.Add(k.Path);
            }

            var trackIds = tracks.Where(t => t is not null).Select(t => t.TrackId)
                .Concat(additions.Where(a => a is not null).Select(a => a.TrackId))
                .Concat(kept.Where(k => k is not null).Select(k => k.TrackId)).ToList();
            if (trackIds.Distinct().Count() != trackIds.Count) errors.Add("A track is bound in more than one role (replacement, addition, kept).");
            var fileIds = tracks.Where(t => t is not null).Select(t => t.OriginalTrackFileId).Concat(kept.Where(k => k is not null).Select(k => k.TrackFileId)).ToList();
            if (fileIds.Distinct().Count() != fileIds.Count) errors.Add("Duplicate original or kept track-file IDs.");

            foreach (var l in albumLyrics)
            {
                if (l is null) { errors.Add("Null album lyric binding."); continue; }
                var label = $"Album lyric {l.LyricFileId}";
                if (l.LyricFileId <= 0) errors.Add($"{label}: LyricFileId must be positive.");
                var owner = tracks.FirstOrDefault(t => t?.TrackId == l.TrackId);
                if (owner is null && !kept.Any(k => k?.TrackId == l.TrackId)) errors.Add($"{label}: its track must be a bound replacement or kept track.");
                if (!IsSafeRelative(l.RelativePath, style)) errors.Add($"{label}: relative path is not a safe relative path.");
                RequireUnder(errors, label, "Path", l.Path, binding.ArtistRoot, style);
                if (IsSafeRelative(l.RelativePath, style) && binding.ArtistRoot is not null &&
                    !string.Equals(JoinUnder(binding.ArtistRoot, l.RelativePath, style), l.Path, StringComparison.Ordinal))
                    errors.Add($"{label}: Path is not ArtistRoot joined with the registered relative path.");
                RequireUnder(errors, label, "BackupPath", l.BackupPath, binding.OperationRoot, style);
                RequireContent(errors, label, "Content", l.Content);
                if (owner?.Lyric is { } bound && bound.LyricFileId == l.LyricFileId)
                {
                    // The same physical lyric seen twice: it must be the same file and bytes, and is not a second path.
                    if (bound.Path != l.Path || bound.RelativePath != l.RelativePath || bound.Content != l.Content)
                        errors.Add($"{label}: disagrees with the replacement's bound lyric.");
                    allPaths.Add(l.BackupPath);
                }
                else
                {
                    if (owner?.Lyric is not null) errors.Add($"{label}: a replacement may own only its bound lyric.");
                    allPaths.AddRange(new[] { l.Path, l.BackupPath });
                }
            }

            if (albumLyrics.Where(l => l is not null).Select(l => l.LyricFileId).Distinct().Count() != albumLyrics.Count(l => l is not null))
                errors.Add("Duplicate album lyric IDs.");

            var nonNull = allPaths.Where(p => p is not null).ToList();
            // Distinct roles must never alias one another, compared case-insensitively on every
            // host because a NAS may fold case even under a case-sensitive path grammar.
            if (nonNull.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nonNull.Count) errors.Add("Two bound paths alias each other.");
        }

        return errors.ToImmutable();
    }

    /// <summary>Joins a validated relative path under a canonical root and canonicalizes the result; null if unsafe.</summary>
    public static string? JoinUnder(string root, string relative, PathStyle style)
    {
        if (!IsSafeRelative(relative, style)) return null;
        var separator = style == PathStyle.Windows ? '\\' : '/';
        var normalized = style == PathStyle.Windows ? relative.Replace('/', '\\') : relative;
        var check = PathCanonicalizer.Canonicalize(root + separator + normalized, style);
        return check.Verdict == PathVerdict.Ok && PathCanonicalizer.IsStrictlyUnder(check.CanonicalPath!, root, style) ? check.CanonicalPath : null;
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
