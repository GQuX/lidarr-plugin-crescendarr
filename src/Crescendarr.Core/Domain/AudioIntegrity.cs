namespace Crescendarr.Core.Domain;

/// <summary>
/// M22: structural integrity of an incoming MPEG audio file, pure (bytes in, verdict out).
/// Real SABnzbd reported a job "Completed" with an article missing when the NZB carried no par2 data, and stock Lidarr's
/// client exposes no article-health signal. A download with a hole in it must not replace a good library file, so every
/// incoming MP3 must be an unbroken chain of valid MPEG frames from the end of its ID3v2 tag to the end of the file
/// (ignoring a trailing ID3v1 tag and an APEv2 tag). This is a necessary check, not proof of perfect audio: a damaged
/// frame payload with intact headers is not detected, and non-MPEG formats are not checked here.
/// </summary>
public static class AudioIntegrity
{
    private static readonly int[,] BitratesV1 =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, -1 }, // Layer I
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, -1 },    // Layer II
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 },     // Layer III
    };

    private static readonly int[,] BitratesV2 =
    {
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, -1 },    // Layer I
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 },         // Layer II and III
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 },
    };

    private static readonly int[] SampleRatesV1 = { 44100, 48000, 32000 };

    /// <summary>Null when the file is a continuous MPEG frame chain; otherwise the reason and byte offset.</summary>
    public static string? Mp3Problem(ReadOnlySpan<byte> data)
    {
        var position = 0;
        if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            var size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
            position = 10 + size + ((data[5] & 0x10) != 0 ? 10 : 0);
        }

        var end = data.Length;
        if (end - position >= 128 && data[end - 128] == 'T' && data[end - 127] == 'A' && data[end - 126] == 'G') end -= 128;
        if (end - position >= 32 && data.Slice(end - 32, 8).SequenceEqual("APETAGEX"u8))
        {
            var tag = data.Slice(end - 32, 32);
            var size = tag[12] | (tag[13] << 8) | (tag[14] << 16) | (tag[15] << 24);
            var hasHeader = (tag[23] & 0x80) != 0;
            end -= size + (hasHeader ? 32 : 0);
        }

        if (position >= end) return "no audio frames after the tags";
        var frames = 0;
        while (position < end)
        {
            if (end - position < 4)
                return $"trailing {end - position} bytes at {position} do not form a complete MPEG frame header";
            var length = FrameLength(data.Slice(position, 4));
            if (length <= 0) return $"lost MPEG frame sync at byte {position} after {frames} frames (hole or corruption)";
            if (length > end - position)
                return $"truncated MPEG frame at byte {position} after {frames} frames (expected {length} bytes, found {end - position})";
            position += length;
            frames++;
        }

        // Even a complete final header is not evidence of a complete last frame.
        // A file cut short after that header must fail closed, including one-byte truncations.
        return frames == 0 ? "no audio frames" : null;
    }

    private static int FrameLength(ReadOnlySpan<byte> h)
    {
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return 0;
        var version = (h[1] >> 3) & 3;      // 0 = MPEG 2.5, 1 = reserved, 2 = MPEG 2, 3 = MPEG 1
        var layer = (h[1] >> 1) & 3;        // 1 = III, 2 = II, 3 = I, 0 = reserved
        var bitrateIndex = h[2] >> 4;
        var rateIndex = (h[2] >> 2) & 3;
        var padding = (h[2] >> 1) & 1;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return 0;
        var layerRow = 3 - layer;           // 0 = Layer I, 1 = II, 2 = III
        var kbps = version == 3 ? BitratesV1[layerRow, bitrateIndex] : BitratesV2[layerRow, bitrateIndex];
        var rate = SampleRatesV1[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        if (kbps <= 0) return 0;
        if (layer == 3) return (12 * kbps * 1000 / rate + padding) * 4;
        if (layer == 1 && version != 3) return 72 * kbps * 1000 / rate + padding;
        return 144 * kbps * 1000 / rate + padding;
    }
}
