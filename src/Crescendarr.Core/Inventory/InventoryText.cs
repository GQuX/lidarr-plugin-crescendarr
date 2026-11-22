using System.Text;

namespace Crescendarr.Core.Inventory;

/// <summary>Pure checks for untrusted strings reported by download clients.</summary>
internal static class InventoryText
{
    public const int MaxDownloadIdLength = 256;
    public const int MaxMetadataLength = 1024;
    public const int MaxEvidenceLength = 1024;

    /// <summary>Returns a short reason when the string contains characters that could spoof or corrupt identity.</summary>
    public static string? UnsafeCharacter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) return "control-character";
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF') return "invisible-character";
            if (ch is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069') return "bidirectional-override";
            if (ch == '\uFFFD') return "replacement-character";
        }

        try
        {
            if (!value.IsNormalized(NormalizationForm.FormC)) return "not-nfc-normalized";
        }
        catch (ArgumentException)
        {
            return "invalid-unicode";
        }

        return null;
    }

    /// <summary>Non-empty, bounded, no surrounding whitespace, no unsafe characters.</summary>
    public static bool IsCleanToken(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength) return false;
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) return false;
        return UnsafeCharacter(value) is null;
    }

    public static bool IsValidDownloadId(string? value) => IsCleanToken(value, MaxDownloadIdLength);

    /// <summary>Optional metadata: null is fine; present values must be clean and bounded.</summary>
    public static bool IsCleanOptional(string? value) =>
        value is null || (value.Length <= MaxMetadataLength && UnsafeCharacter(value) is null);

    /// <summary>ASCII-only provider implementation names (stock class names such as Sabnzbd).</summary>
    public static bool IsImplementationToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch)) return false;
        }

        return true;
    }

    /// <summary>
    /// Keeps untrusted evidence for humans without letting it carry control characters.
    /// The result is bounded and only ever used as evidence, never as identity.
    /// </summary>
    public static string? Evidence(string? value)
    {
        if (value is null) return null;
        var builder = new StringBuilder(Math.Min(value.Length, MaxEvidenceLength));
        foreach (var ch in value)
        {
            if (builder.Length >= MaxEvidenceLength) break;
            builder.Append(char.IsControl(ch) || char.IsSurrogate(ch) || ch is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069'
                ? '?'
                : ch);
        }

        return builder.ToString();
    }
}
