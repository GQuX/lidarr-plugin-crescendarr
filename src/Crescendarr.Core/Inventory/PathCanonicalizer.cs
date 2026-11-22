using System.Collections.Immutable;

namespace Crescendarr.Core.Inventory;

/// <summary>Path grammar of the Lidarr host that reports the output path (not of this process).</summary>
public enum PathStyle
{
    /// <summary>Linux/container hosts: case-sensitive, '/' only.</summary>
    Posix = 1,

    /// <summary>Windows hosts: drive-letter paths, case-insensitive.</summary>
    Windows = 2,
}

public enum PathVerdict
{
    Ok = 1,
    Missing = 2,
    Unsafe = 3,
    OutsideAllowedRoots = 4,
}

public readonly record struct PathCheck(PathVerdict Verdict, string? CanonicalPath, string? MatchedRoot, string? Detail);

/// <summary>
/// Purely lexical canonicalization. It performs NO filesystem access, so it cannot see
/// symlinks, bind mounts, SMB case folding or mount changes; it only refuses strings
/// whose meaning would be ambiguous. Host revalidation is still required before any write.
/// </summary>
public static class PathCanonicalizer
{
    public const int MaxLength = 4096;

    private static readonly ImmutableHashSet<string> WindowsReservedNames = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9");

    public static PathCheck Canonicalize(string? raw, PathStyle style)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(PathVerdict.Missing, null, null, "missing");
        if (raw.Length > MaxLength) return Unsafe("too-long");
        if (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[^1])) return Unsafe("surrounding-whitespace");
        var character = InventoryText.UnsafeCharacter(raw);
        if (character is not null) return Unsafe(character);
        if (raw.Contains("://", StringComparison.Ordinal)) return Unsafe("uri");

        return style switch
        {
            PathStyle.Posix => CanonicalizePosix(raw),
            PathStyle.Windows => CanonicalizeWindows(raw),
            _ => Unsafe("unknown-path-style"),
        };
    }

    /// <summary>Canonicalizes and requires the path to be strictly below exactly one allowed root.</summary>
    public static PathCheck CheckOutput(string? raw, PathStyle style, ImmutableArray<string> allowedRoots)
    {
        var check = Canonicalize(raw, style);
        if (check.Verdict != PathVerdict.Ok) return check;

        string? match = null;
        foreach (var root in allowedRoots)
        {
            if (!IsStrictlyUnder(check.CanonicalPath!, root, style)) continue;
            if (match is not null) return Unsafe("matches-multiple-roots");
            match = root;
        }

        return match is null
            ? new(PathVerdict.OutsideAllowedRoots, check.CanonicalPath, null, "outside-allowed-roots")
            : check with { MatchedRoot = match };
    }

    /// <summary>True only when <paramref name="path"/> is a proper descendant of <paramref name="root"/>, segment-wise.</summary>
    public static bool IsStrictlyUnder(string path, string root, PathStyle style)
    {
        var pathSegments = Segments(path, style);
        var rootSegments = Segments(root, style);
        if (pathSegments.Length <= rootSegments.Length) return false;

        var comparison = Comparison(style);
        for (var i = 0; i < rootSegments.Length; i++)
        {
            if (!string.Equals(pathSegments[i], rootSegments[i], comparison)) return false;
        }

        return true;
    }

    public static bool PathsEqual(string a, string b, PathStyle style) => string.Equals(a, b, Comparison(style));

    public static StringComparison Comparison(PathStyle style) =>
        style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string[] Segments(string canonical, PathStyle style) => style == PathStyle.Windows
        ? canonical.Split('\\', StringSplitOptions.RemoveEmptyEntries)
        : canonical.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static PathCheck CanonicalizePosix(string raw)
    {
        if (raw[0] != '/') return Unsafe("not-absolute");
        // POSIX leaves a leading "//" implementation-defined; refuse rather than guess.
        if (raw.StartsWith("//", StringComparison.Ordinal)) return Unsafe("implementation-defined-root");
        // A backslash is a legal POSIX filename character but signals a foreign path grammar.
        if (raw.Contains('\\', StringComparison.Ordinal)) return Unsafe("backslash-in-posix-path");

        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..") return Unsafe("dot-segment");
        }

        return Ok("/" + string.Join('/', segments));
    }

    private static PathCheck CanonicalizeWindows(string raw)
    {
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            return Unsafe("unc-or-device-path");
        if (raw.Length < 3 || !char.IsAsciiLetter(raw[0]) || raw[1] != ':' || (raw[2] != '\\' && raw[2] != '/'))
            return Unsafe("not-absolute");

        var drive = char.ToUpperInvariant(raw[0]) + ":";
        var rest = raw[3..].Replace('/', '\\');
        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..") return Unsafe("dot-segment");
            if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0) return Unsafe("invalid-windows-character");
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return Unsafe("trailing-dot-or-space");
            var stem = segment.Split('.')[0];
            if (WindowsReservedNames.Contains(stem)) return Unsafe("reserved-device-name");
        }

        return Ok(drive + "\\" + string.Join('\\', segments));
    }

    private static PathCheck Ok(string canonical) => new(PathVerdict.Ok, canonical, null, null);

    private static PathCheck Unsafe(string detail) => new(PathVerdict.Unsafe, null, null, detail);
}
