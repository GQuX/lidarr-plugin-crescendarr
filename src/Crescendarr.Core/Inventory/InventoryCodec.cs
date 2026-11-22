using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Crescendarr.Core.Inventory;

public sealed record InventoryLoadResult(InventoryState? State, string? Error)
{
    public bool Succeeded => State is not null && Error is null;
}

/// <summary>
/// Canonical text encoding of <see cref="InventoryState"/>. It converts between a string
/// and a value only; it does not read or write storage. Decoding is strict and fails
/// closed: there is no fallback to an empty inventory, because that would erase
/// ambiguity and review evidence.
/// </summary>
public static class InventoryCodec
{
    public const string Format = "crescendarr.download-inventory";
    private const int MaxDocumentChars = 32 * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };
    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    public static string Encode(InventoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = EncodePayload(state);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schema", InventoryState.SchemaVersion);
            writer.WriteString("payloadSha256", Sha256(payload));
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payload, skipInputValidation: false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Digest of one item's canonical encoding; a future executor can use it as an optimistic-concurrency token.</summary>
    public static string EvidenceDigest(ItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) WriteItem(writer, item);
        return Sha256(buffer.WrittenSpan);
    }

    public static InventoryLoadResult Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("empty document");
        if (text.Length > MaxDocumentChars) return Fail("document too large");

        try
        {
            using var document = JsonDocument.Parse(text, ReaderOptions);
            var root = document.RootElement;
            RequireExactProperties(root, "format", "schema", "payloadSha256", "payload");
            if (ReadString(root, "format") != Format) return Fail("unknown format");
            if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out var schema) || schema != InventoryState.SchemaVersion)
                return Fail("unsupported schema version");

            var state = ReadPayload(root.GetProperty("payload"));
            var canonical = EncodePayload(state);
            if (!string.Equals(Sha256(canonical), ReadString(root, "payloadSha256"), StringComparison.Ordinal))
                return Fail("payload digest mismatch");

            var invariant = CheckInvariants(state);
            return invariant is null ? new InventoryLoadResult(state, null) : Fail(invariant);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            return Fail("malformed document: " + error.GetType().Name);
        }
    }

    private static InventoryLoadResult Fail(string error) => new(null, error);

    private static byte[] EncodePayload(InventoryState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("clients");
            foreach (var client in state.Clients.Values) WriteClient(writer, client);
            writer.WriteEndArray();
            writer.WriteStartArray("items");
            foreach (var item in state.Items.Values) WriteItem(writer, item);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteClient(Utf8JsonWriter w, ClientRecord c)
    {
        w.WriteStartObject();
        w.WriteNumber("definitionId", c.DefinitionId);
        w.WriteString("implementation", c.Implementation);
        w.WriteString("name", c.Name);
        w.WriteNumber("lastSequence", c.LastSequence);
        WriteNullable(w, "lastPollTicks", c.LastPollTicks);
        WriteNullable(w, "lastSuccessTicks", c.LastSuccessTicks);
        w.WriteNumber("consecutiveFailures", c.ConsecutiveFailures);
        WriteNullable(w, "lastFailureReason", c.LastFailureReason);
        w.WriteNumber("unkeyableItemsTotal", c.UnkeyableItemsTotal);
        WriteReasons(w, "stickyReasons", c.StickyReasons);
        WriteReasons(w, "lastPollReasons", c.LastPollReasons);
        w.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter w, ItemRecord i)
    {
        w.WriteStartObject();
        w.WriteNumber("clientDefinitionId", i.Key.ClientDefinitionId);
        w.WriteString("downloadId", i.Key.DownloadId);
        w.WriteString("state", i.State.ToString());
        WriteReasons(w, "reasons", i.Reasons);
        w.WriteNumber("firstSeenTicks", i.FirstSeenTicks);
        w.WriteNumber("lastSeenTicks", i.LastSeenTicks);
        w.WriteNumber("lastSeenSequence", i.LastSeenSequence);
        w.WriteString("lastStatus", i.LastStatus.ToString());
        WriteNullable(w, "title", i.Title);
        WriteNullable(w, "category", i.Category);
        WriteNullable(w, "currentPath", i.CurrentPath);
        WriteNullable(w, "currentRoot", i.CurrentRoot);
        WriteNullable(w, "anchoredPath", i.AnchoredPath);
        WriteNullable(w, "anchoredRoot", i.AnchoredRoot);
        WriteNullable(w, "rawPathEvidence", i.RawPathEvidence);
        WriteNullable(w, "totalBytes", i.TotalBytes);
        WriteNullable(w, "remainingBytes", i.RemainingBytes);
        w.WriteBoolean("everCompleted", i.EverCompleted);
        w.WriteNumber("stablePollCount", i.StablePollCount);
        WriteNullable(w, "stableSinceTicks", i.StableSinceTicks);
        w.WriteNumber("consecutiveAbsences", i.ConsecutiveAbsences);
        w.WriteStartArray("variantPaths");
        foreach (var path in i.VariantPaths) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, long? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteNumber(name, value.Value);
    }

    private static void WriteReasons(Utf8JsonWriter w, string name, ImmutableSortedSet<InventoryReason> reasons)
    {
        w.WriteStartArray(name);
        foreach (var reason in reasons) w.WriteStringValue(reason.ToString());
        w.WriteEndArray();
    }

    private static InventoryState ReadPayload(JsonElement payload)
    {
        RequireExactProperties(payload, "clients", "items");
        var clients = ImmutableSortedDictionary.CreateBuilder<int, ClientRecord>();
        foreach (var element in ReadArray(payload, "clients"))
        {
            var client = ReadClient(element);
            if (!clients.TryAdd(client.DefinitionId, client)) throw new FormatException("duplicate client");
        }

        var items = ImmutableSortedDictionary.CreateBuilder<ItemKey, ItemRecord>();
        foreach (var element in ReadArray(payload, "items"))
        {
            var item = ReadItem(element);
            if (!items.TryAdd(item.Key, item)) throw new FormatException("duplicate item");
        }

        return new InventoryState(clients.ToImmutable(), items.ToImmutable());
    }

    private static ClientRecord ReadClient(JsonElement e)
    {
        RequireExactProperties(e, "definitionId", "implementation", "name", "lastSequence", "lastPollTicks", "lastSuccessTicks",
            "consecutiveFailures", "lastFailureReason", "unkeyableItemsTotal", "stickyReasons", "lastPollReasons");
        return new ClientRecord(
            ReadInt(e, "definitionId"),
            ReadString(e, "implementation"),
            ReadString(e, "name"),
            ReadLong(e, "lastSequence"),
            ReadNullableLong(e, "lastPollTicks"),
            ReadNullableLong(e, "lastSuccessTicks"),
            ReadInt(e, "consecutiveFailures"),
            ReadNullableString(e, "lastFailureReason"),
            ReadLong(e, "unkeyableItemsTotal"),
            ReadReasons(e, "stickyReasons"),
            ReadReasons(e, "lastPollReasons"));
    }

    private static ItemRecord ReadItem(JsonElement e)
    {
        RequireExactProperties(e, "clientDefinitionId", "downloadId", "state", "reasons", "firstSeenTicks", "lastSeenTicks",
            "lastSeenSequence", "lastStatus", "title", "category", "currentPath", "currentRoot", "anchoredPath", "anchoredRoot",
            "rawPathEvidence", "totalBytes", "remainingBytes", "everCompleted", "stablePollCount", "stableSinceTicks", "consecutiveAbsences", "variantPaths");
        return new ItemRecord(
            new ItemKey(ReadInt(e, "clientDefinitionId"), ReadString(e, "downloadId")),
            ReadEnum<InventoryItemState>(e.GetProperty("state")),
            ReadReasons(e, "reasons"),
            ReadLong(e, "firstSeenTicks"),
            ReadLong(e, "lastSeenTicks"),
            ReadLong(e, "lastSeenSequence"),
            ReadEnum<ClientItemStatus>(e.GetProperty("lastStatus")),
            ReadNullableString(e, "title"),
            ReadNullableString(e, "category"),
            ReadNullableString(e, "currentPath"),
            ReadNullableString(e, "currentRoot"),
            ReadNullableString(e, "anchoredPath"),
            ReadNullableString(e, "anchoredRoot"),
            ReadNullableString(e, "rawPathEvidence"),
            ReadNullableLong(e, "totalBytes"),
            ReadNullableLong(e, "remainingBytes"),
            ReadBool(e, "everCompleted"),
            ReadInt(e, "stablePollCount"),
            ReadNullableLong(e, "stableSinceTicks"),
            ReadInt(e, "consecutiveAbsences"),
            ReadPaths(e, "variantPaths"));
    }

    private static string? CheckInvariants(InventoryState state)
    {
        foreach (var client in state.Clients.Values)
        {
            if (client.DefinitionId <= 0 || !InventoryText.IsImplementationToken(client.Implementation)) return "invalid client identity";
            if (client.LastSequence < -1 || client.ConsecutiveFailures < 0 || client.UnkeyableItemsTotal < 0) return "negative client counter";
        }

        foreach (var item in state.Items.Values)
        {
            if (!state.Clients.ContainsKey(item.Key.ClientDefinitionId)) return "item references unknown client";
            if (!InventoryText.IsValidDownloadId(item.Key.DownloadId)) return "invalid download id";
            if (item.StablePollCount < 0 || item.ConsecutiveAbsences < 0 || item.LastSeenSequence < 0) return "negative item counter";
            // Wall-clock fields may legitimately run backwards after an observer clock
            // regression; ordering is checked by sequence, never by ticks.
            if (item.LastSeenSequence > state.Clients[item.Key.ClientDefinitionId].LastSequence) return "item newer than its client";
            if (item.State == InventoryItemState.CompletedStable &&
                (item.AnchoredPath is null || item.AnchoredRoot is null || !item.EverCompleted || item.StableSinceTicks is null || item.StablePollCount < 2))
                return "stable item lacks stability evidence";
            if (item.IsSticky && item.Reasons.IsEmpty) return "review state without a reason";
            if (item.State == InventoryItemState.MissingFromClient && item.ConsecutiveAbsences == 0) return "missing item without absences";
            if (!item.VariantPaths.IsEmpty && !item.IsSticky) return "variant paths on a non-review record";
            if (item.VariantPaths.Any(p => p.Length == 0 || InventoryText.UnsafeCharacter(p) is not null)) return "unsafe variant path";
        }

        return null;
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("expected object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException("duplicate property");
            if (Array.IndexOf(names, property.Name) < 0) throw new FormatException("unexpected property");
        }

        if (seen.Count != names.Length) throw new FormatException("missing property");
    }

    private static JsonElement.ArrayEnumerator ReadArray(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException("expected array");
        return value.EnumerateArray();
    }

    private static string ReadString(JsonElement e, string name) =>
        ReadNullableString(e, name) ?? throw new FormatException("unexpected null");

    private static string? ReadNullableString(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new FormatException("expected string"),
        };
    }

    private static long ReadLong(JsonElement e, string name) =>
        ReadNullableLong(e, name) ?? throw new FormatException("unexpected null");

    private static long? ReadNullableLong(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result)) throw new FormatException("expected integer");
        return result;
    }

    private static int ReadInt(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result)) throw new FormatException("expected int32");
        return result;
    }

    private static bool ReadBool(JsonElement e, string name)
    {
        var value = e.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException("expected boolean"),
        };
    }

    private static T ReadEnum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("expected enum name");
        var text = value.GetString()!;
        // Enum.TryParse accepts numeric strings and comma lists; only exact declared names are allowed.
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]) || !Enum.TryParse<T>(text, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            throw new FormatException("unknown enum name");
        return parsed;
    }

    private static ImmutableSortedSet<string> ReadPaths(JsonElement e, string name)
    {
        var builder = ItemRecord.NoPaths.ToBuilder();
        foreach (var element in ReadArray(e, name))
        {
            if (element.ValueKind != JsonValueKind.String || !builder.Add(element.GetString()!)) throw new FormatException("invalid or duplicate variant path");
        }

        return builder.ToImmutable();
    }

    private static ImmutableSortedSet<InventoryReason> ReadReasons(JsonElement e, string name)
    {
        var builder = ImmutableSortedSet.CreateBuilder<InventoryReason>();
        foreach (var element in ReadArray(e, name))
        {
            if (!builder.Add(ReadEnum<InventoryReason>(element))) throw new FormatException("duplicate reason");
        }

        return builder.ToImmutable();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
