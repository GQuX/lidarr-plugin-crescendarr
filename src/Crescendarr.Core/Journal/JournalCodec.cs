using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crescendarr.Core.Inventory;

namespace Crescendarr.Core.Journal;

public sealed record JournalLoadResult(OperationManifest? Manifest, string? Error)
{
    public bool Succeeded => Manifest is not null && Error is null;
}

/// <summary>
/// Canonical text form of a manifest. Converts string &lt;-&gt; value only; never touches storage.
/// Decoding is strict and fails closed with no fallback. The SHA-256 values are UNKEYED: they
/// detect accidental corruption and truncation, not deliberate tampering with a recomputed hash.
/// </summary>
public static class JournalCodec
{
    public const string Format = "crescendarr.operation-journal";
    public const int MaxDocumentChars = 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };
    private static readonly JsonDocumentOptions ReaderOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 };

    public static string Encode(OperationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var payload = Write(w => WritePayload(w, manifest));
        if (payload.Length > MaxDocumentChars) throw new JournalTransitionException("Manifest exceeds the size cap.");
        var document = Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", OperationManifest.FormatVersion);
            w.WriteString("payloadSha256", Sha256(payload));
            w.WritePropertyName("payload");
            w.WriteRawValue(payload, skipInputValidation: false);
            w.WriteEndObject();
        });
        return Encoding.UTF8.GetString(document);
    }

    /// <summary>Digest of the immutable binding; identical for every generation of one operation.</summary>
    public static string BindingDigest(OperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Sha256(Write(w => WriteBinding(w, binding)));
    }

    public static JournalLoadResult Decode(string? text, string? expectedOperationId = null, string? expectedBindingDigest = null)
    {
        if (string.IsNullOrEmpty(text)) return Fail("empty document");
        if (text.Length > MaxDocumentChars + 512) return Fail("document exceeds size cap");
        try
        {
            using var document = JsonDocument.Parse(text, ReaderOptions);
            var root = document.RootElement;
            Exact(root, "format", "version", "payloadSha256", "payload");
            if (Str(root, "format") != Format) return Fail("unknown format");
            if (Int(root, "version") != OperationManifest.FormatVersion) return Fail("unsupported version");

            var manifest = ReadPayload(root.GetProperty("payload"));
            var canonical = Write(w => WritePayload(w, manifest));
            if (!string.Equals(Sha256(canonical), Str(root, "payloadSha256"), StringComparison.Ordinal)) return Fail("payload digest mismatch");

            var invariant = CheckInvariants(manifest);
            if (invariant is not null) return Fail(invariant);
            if (expectedOperationId is not null && !string.Equals(manifest.Binding.OperationId, expectedOperationId, StringComparison.Ordinal))
                return Fail("operation ID does not match its location");
            if (expectedBindingDigest is not null && !string.Equals(manifest.BindingDigest, expectedBindingDigest, StringComparison.Ordinal))
                return Fail("binding differs from the operation's original binding");
            return new JournalLoadResult(manifest, null);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            return Fail("malformed document: " + error.GetType().Name);
        }
    }

    private static string? CheckInvariants(OperationManifest m)
    {
        var errors = JournalValidation.Validate(m.Binding);
        if (!errors.IsEmpty) return "invalid binding: " + errors[0];
        if (!string.Equals(BindingDigest(m.Binding), m.BindingDigest, StringComparison.Ordinal)) return "binding digest mismatch";
        if (m.Generation < 1) return "generation must be positive";
        if (m.Progress.Length != m.Binding.Tracks.Length) return "progress does not cover every bound track";
        for (var i = 0; i < m.Progress.Length; i++)
        {
            var p = m.Progress[i];
            var t = m.Binding.Tracks[i];
            if (p.TrackId != t.TrackId) return "progress order differs from binding order";
            if (p.InterruptedAttempts < 0) return "negative attempt count";
            if (p.Phase == TrackPhase.LyricDetached && t.Lyric is null) return "lyric phase without a bound lyric";
            if (p.Phase == TrackPhase.LyricRelinked && t.Lyric is null) return "lyric phase without a bound lyric";
            if ((p.Phase >= TrackPhase.Upgraded) != (p.NewTrackFileId is not null)) return "new track-file ID inconsistent with phase";
            if ((p.Phase == TrackPhase.LyricRelinked) != (p.NewLyricFileId is not null)) return "new lyric ID inconsistent with phase";
            if (p.Phase > TrackPhase.BackupVerified && m.Progress.Any(o => o.Phase < TrackPhase.BackupVerified)) return "destructive phase before every backup was verified";
            if (p.ObservedOriginalTrackFileId is { } observed && (observed <= 0 || observed == t.OriginalTrackFileId)) return "invalid rebase evidence";
        }

        var additions = m.Additions.IsDefault ? ImmutableArray<AdditionProgress>.Empty : m.Additions;
        if (additions.Length != m.Binding.Additions.Length) return "addition progress does not cover every bound addition";
        for (var i = 0; i < additions.Length; i++)
        {
            var a = additions[i];
            if (a.TrackId != m.Binding.Additions[i].TrackId) return "addition progress order differs from binding order";
            if (a.InterruptedAttempts < 0) return "negative attempt count";
            if ((a.Phase == AdditionPhase.Added) != (a.NewTrackFileId is not null)) return "addition track-file ID inconsistent with phase";
            if (a.Phase == AdditionPhase.Added && a.InFlight) return "added addition still in flight";
            if ((a.Phase == AdditionPhase.Added || a.InFlight) && !JournalEngine.AllUpgradesFinal(m)) return "addition before every replacement was final";
        }

        if (m.Progress.Count(p => p.InFlight is not null) + additions.Count(a => a.InFlight) > 1) return "more than one step in flight";
        if (m.Status == OperationStatus.Completed && additions.Any(a => a.Phase != AdditionPhase.Added)) return "completed operation with unfinished additions";
        if (m.InFlightTrack is { } busy)
        {
            if (m.Status != OperationStatus.Active && m.Status != OperationStatus.RequiresReview) return "step in flight on a completed operation";
            var expected = NextStepIgnoringFlight(m, busy.TrackId);
            if (expected != busy.InFlight) return "in-flight step is not the next allowed step";
        }

        if (m.Status == OperationStatus.Completed && m.Binding.Tracks.Any(t => !JournalEngine.IsFinal(m, t.TrackId))) return "completed operation with unfinished tracks";
        if (m.Status == OperationStatus.RequiresReview && m.ReviewReasons.IsEmpty) return "review without a reason";
        if (m.ReviewReasons.Length > JournalEngine.MaxReviewReasons) return "too many review reasons";
        return null;
    }

    private static JournalStep? NextStepIgnoringFlight(OperationManifest m, int trackId)
    {
        var cleared = m with { Progress = m.Progress.Select(p => p with { InFlight = null }).ToImmutableArray() };
        return JournalEngine.NextStep(cleared, trackId);
    }

    // ---------- writing ----------

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) body(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePayload(Utf8JsonWriter w, OperationManifest m)
    {
        w.WriteStartObject();
        w.WriteNumber("generation", m.Generation);
        w.WriteString("status", m.Status.ToString());
        w.WriteStartArray("reviewReasons");
        foreach (var reason in m.ReviewReasons) w.WriteStringValue(reason);
        w.WriteEndArray();
        w.WriteString("bindingDigest", m.BindingDigest);
        w.WritePropertyName("binding");
        WriteBinding(w, m.Binding);
        w.WriteStartArray("progress");
        foreach (var p in m.Progress)
        {
            w.WriteStartObject();
            w.WriteNumber("trackId", p.TrackId);
            w.WriteString("phase", p.Phase.ToString());
            NullableString(w, "inFlight", p.InFlight?.ToString());
            w.WriteNumber("interruptedAttempts", p.InterruptedAttempts);
            NullableInt(w, "newTrackFileId", p.NewTrackFileId);
            NullableInt(w, "newLyricFileId", p.NewLyricFileId);
            NullableInt(w, "observedOriginalTrackFileId", p.ObservedOriginalTrackFileId);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("additions");
        foreach (var a in m.Additions)
        {
            w.WriteStartObject();
            w.WriteNumber("trackId", a.TrackId);
            w.WriteString("phase", a.Phase.ToString());
            w.WriteBoolean("inFlight", a.InFlight);
            w.WriteNumber("interruptedAttempts", a.InterruptedAttempts);
            NullableInt(w, "newTrackFileId", a.NewTrackFileId);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter w, OperationBinding b)
    {
        w.WriteStartObject();
        w.WriteString("operationId", b.OperationId);
        if (b.Download is { } d)
        {
            w.WriteStartObject("download");
            w.WriteNumber("clientDefinitionId", d.ClientDefinitionId);
            w.WriteString("downloadId", d.DownloadId);
            NullableString(w, "inventoryEvidenceDigest", d.InventoryEvidenceDigest);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("download");
        }

        w.WriteNumber("albumId", b.AlbumId);
        w.WriteString("releaseGroupMbid", b.ReleaseGroupMbid);
        w.WriteNumber("monitoredReleaseId", b.MonitoredReleaseId);
        w.WriteString("monitoredReleaseMbid", b.MonitoredReleaseMbid);
        w.WriteString("pathStyle", b.PathStyle.ToString());
        w.WriteString("libraryRoot", b.LibraryRoot);
        w.WriteString("stagingRoot", b.StagingRoot);
        w.WriteString("operationRoot", b.OperationRoot);
        w.WriteString("artistRoot", b.ArtistRoot);
        w.WriteStartArray("tracks");
        foreach (var t in b.Tracks)
        {
            w.WriteStartObject();
            w.WriteNumber("trackId", t.TrackId);
            w.WriteString("recordingMbid", t.RecordingMbid);
            w.WriteNumber("originalTrackFileId", t.OriginalTrackFileId);
            w.WriteString("originalPath", t.OriginalPath);
            w.WriteString("destinationPath", t.DestinationPath);
            w.WriteString("incomingPath", t.IncomingPath);
            WriteContent(w, "original", t.Original);
            WriteContent(w, "incoming", t.Incoming);
            w.WriteString("backupAudioPath", t.BackupAudioPath);
            if (t.Lyric is { } l)
            {
                w.WriteStartObject("lyric");
                w.WriteNumber("lyricFileId", l.LyricFileId);
                w.WriteString("relativePath", l.RelativePath);
                w.WriteString("path", l.Path);
                WriteContent(w, "content", l.Content);
                w.WriteString("backupPath", l.BackupPath);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("lyric");
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("additions");
        foreach (var a in b.Additions)
        {
            w.WriteStartObject();
            w.WriteNumber("trackId", a.TrackId);
            w.WriteString("recordingMbid", a.RecordingMbid);
            w.WriteString("destinationPath", a.DestinationPath);
            w.WriteString("incomingPath", a.IncomingPath);
            WriteContent(w, "incoming", a.Incoming);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("kept");
        foreach (var k in b.Kept)
        {
            w.WriteStartObject();
            w.WriteNumber("trackId", k.TrackId);
            w.WriteString("recordingMbid", k.RecordingMbid);
            w.WriteNumber("trackFileId", k.TrackFileId);
            w.WriteString("path", k.Path);
            WriteContent(w, "content", k.Content);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("albumLyrics");
        foreach (var l in b.AlbumLyrics)
        {
            w.WriteStartObject();
            w.WriteNumber("lyricFileId", l.LyricFileId);
            w.WriteNumber("trackId", l.TrackId);
            w.WriteString("relativePath", l.RelativePath);
            w.WriteString("path", l.Path);
            WriteContent(w, "content", l.Content);
            w.WriteString("backupPath", l.BackupPath);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter w, string name, ContentId c)
    {
        w.WriteStartObject(name);
        w.WriteString("sha256", c.Sha256);
        w.WriteNumber("length", c.Length);
        w.WriteEndObject();
    }

    private static void NullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static void NullableInt(Utf8JsonWriter w, string name, int? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    // ---------- reading ----------

    private static OperationManifest ReadPayload(JsonElement e)
    {
        Exact(e, "generation", "status", "reviewReasons", "bindingDigest", "binding", "progress", "additions");
        var reasons = Array(e, "reviewReasons").Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : throw new FormatException("reason")).ToImmutableArray();
        var progress = Array(e, "progress").Select(p =>
        {
            Exact(p, "trackId", "phase", "inFlight", "interruptedAttempts", "newTrackFileId", "newLyricFileId", "observedOriginalTrackFileId");
            var inFlight = NullableStr(p, "inFlight");
            return new TrackProgress(
                Int(p, "trackId"),
                ParseEnum<TrackPhase>(Str(p, "phase")),
                inFlight is null ? null : ParseEnum<JournalStep>(inFlight),
                Int(p, "interruptedAttempts"),
                NullableInt(p, "newTrackFileId"),
                NullableInt(p, "newLyricFileId"))
            {
                ObservedOriginalTrackFileId = NullableInt(p, "observedOriginalTrackFileId"),
            };
        }).ToImmutableArray();

        var additions = Array(e, "additions").Select(a =>
        {
            Exact(a, "trackId", "phase", "inFlight", "interruptedAttempts", "newTrackFileId");
            var flight = a.GetProperty("inFlight");
            if (flight.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("expected boolean");
            return new AdditionProgress(Int(a, "trackId"), ParseEnum<AdditionPhase>(Str(a, "phase")), flight.GetBoolean(), Int(a, "interruptedAttempts"), NullableInt(a, "newTrackFileId"));
        }).ToImmutableArray();

        return new OperationManifest(
            Long(e, "generation"),
            ParseEnum<OperationStatus>(Str(e, "status")),
            reasons,
            ReadBinding(e.GetProperty("binding")),
            Str(e, "bindingDigest"),
            progress)
        {
            Additions = additions,
        };
    }

    private static OperationBinding ReadBinding(JsonElement e)
    {
        Exact(e, "operationId", "download", "albumId", "releaseGroupMbid", "monitoredReleaseId", "monitoredReleaseMbid", "pathStyle", "libraryRoot", "stagingRoot", "operationRoot", "artistRoot", "tracks", "additions", "kept", "albumLyrics");
        DownloadBinding? download = null;
        var d = e.GetProperty("download");
        if (d.ValueKind != JsonValueKind.Null)
        {
            Exact(d, "clientDefinitionId", "downloadId", "inventoryEvidenceDigest");
            download = new DownloadBinding(Int(d, "clientDefinitionId"), Str(d, "downloadId"), NullableStr(d, "inventoryEvidenceDigest"));
        }

        var tracks = Array(e, "tracks").Select(t =>
        {
            Exact(t, "trackId", "recordingMbid", "originalTrackFileId", "originalPath", "destinationPath", "incomingPath", "original", "incoming", "backupAudioPath", "lyric");
            LyricBinding? lyric = null;
            var l = t.GetProperty("lyric");
            if (l.ValueKind != JsonValueKind.Null)
            {
                Exact(l, "lyricFileId", "relativePath", "path", "content", "backupPath");
                lyric = new LyricBinding(Int(l, "lyricFileId"), Str(l, "relativePath"), Str(l, "path"), Content(l.GetProperty("content")), Str(l, "backupPath"));
            }

            return new TrackBinding(Int(t, "trackId"), Str(t, "recordingMbid"), Int(t, "originalTrackFileId"), Str(t, "originalPath"), Str(t, "destinationPath"),
                Str(t, "incomingPath"), Content(t.GetProperty("original")), Content(t.GetProperty("incoming")), Str(t, "backupAudioPath"), lyric);
        }).ToImmutableArray();

        var additions = Array(e, "additions").Select(a =>
        {
            Exact(a, "trackId", "recordingMbid", "destinationPath", "incomingPath", "incoming");
            return new AdditionBinding(Int(a, "trackId"), Str(a, "recordingMbid"), Str(a, "destinationPath"), Str(a, "incomingPath"), Content(a.GetProperty("incoming")));
        }).ToImmutableArray();
        var kept = Array(e, "kept").Select(k =>
        {
            Exact(k, "trackId", "recordingMbid", "trackFileId", "path", "content");
            return new KeptTrackBinding(Int(k, "trackId"), Str(k, "recordingMbid"), Int(k, "trackFileId"), Str(k, "path"), Content(k.GetProperty("content")));
        }).ToImmutableArray();
        var albumLyrics = Array(e, "albumLyrics").Select(l =>
        {
            Exact(l, "lyricFileId", "trackId", "relativePath", "path", "content", "backupPath");
            return new AlbumLyricBinding(Int(l, "lyricFileId"), Int(l, "trackId"), Str(l, "relativePath"), Str(l, "path"), Content(l.GetProperty("content")), Str(l, "backupPath"));
        }).ToImmutableArray();

        return new OperationBinding(Str(e, "operationId"), download, Int(e, "albumId"), Str(e, "releaseGroupMbid"), Int(e, "monitoredReleaseId"),
            Str(e, "monitoredReleaseMbid"), ParseEnum<PathStyle>(Str(e, "pathStyle")), Str(e, "libraryRoot"), Str(e, "stagingRoot"), Str(e, "operationRoot"), tracks, Str(e, "artistRoot"))
        {
            Additions = additions,
            Kept = kept,
            AlbumLyrics = albumLyrics,
        };
    }

    private static ContentId Content(JsonElement e)
    {
        Exact(e, "sha256", "length");
        return new ContentId(Str(e, "sha256"), Long(e, "length"));
    }

    private static void Exact(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("expected object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in e.EnumerateObject())
        {
            if (!seen.Add(property.Name) || System.Array.IndexOf(names, property.Name) < 0) throw new FormatException("unexpected or duplicate property");
        }

        if (seen.Count != names.Length) throw new FormatException("missing property");
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException("expected array");
        return value.EnumerateArray().ToList();
    }

    private static string Str(JsonElement e, string name) => NullableStr(e, name) ?? throw new FormatException("unexpected null");

    private static string? NullableStr(JsonElement e, string name)
    {
        var v = e.GetProperty(name);
        return v.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => v.GetString(), _ => throw new FormatException("expected string") };
    }

    private static int Int(JsonElement e, string name) => NullableInt(e, name) ?? throw new FormatException("unexpected null");

    private static int? NullableInt(JsonElement e, string name)
    {
        var v = e.GetProperty(name);
        if (v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw new FormatException("expected int32");
    }

    private static long Long(JsonElement e, string name)
    {
        var v = e.GetProperty(name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : throw new FormatException("expected int64");
    }

    private static T ParseEnum<T>(string text) where T : struct, Enum
    {
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]) || !Enum.TryParse<T>(text, false, out var value) || !Enum.IsDefined(value) || value.ToString() != text)
            throw new FormatException("unknown enum name");
        return value;
    }

    private static JournalLoadResult Fail(string error) => new(null, error);

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
