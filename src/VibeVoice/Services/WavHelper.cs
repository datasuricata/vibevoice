namespace VibeVoice.Services;

/// <summary>
/// Utilities for working with raw WAV (RIFF/WAVE PCM) byte arrays.
/// Properly scans the chunk tree instead of assuming a fixed header size,
/// so it handles WAV files that contain extra chunks (LIST, INFO, etc.)
/// before the data chunk — as produced by some TTS backends.
/// </summary>
public static class WavHelper
{
    // "RIFF"(4) + size(4) + "WAVE"(4) = 12
    private const int RiffPreambleSize = 12;

    /// <summary>
    /// Concatenates multiple WAV buffers into a single WAV buffer.
    /// All inputs must share the same sample rate, channels, and bit depth.
    /// </summary>
    public static byte[] Concatenate(IReadOnlyList<byte[]> wavFiles)
    {
        var valid = wavFiles
            .Where(f => f is { Length: > RiffPreambleSize } && IsRiffWave(f))
            .ToList();

        if (valid.Count == 0) return [];
        if (valid.Count == 1) return valid[0];

        var pcmChunks = valid
            .Select(ExtractPcmData)
            .Where(c => c.Length > 0)
            .ToList();

        if (pcmChunks.Count == 0) return [];

        var totalPcm = pcmChunks.Sum(c => c.Length);
        var header = BuildMinimalHeader(valid[0], totalPcm);

        var result = new byte[header.Length + totalPcm];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);

        var writeOffset = header.Length;
        foreach (var chunk in pcmChunks)
        {
            Buffer.BlockCopy(chunk, 0, result, writeOffset, chunk.Length);
            writeOffset += chunk.Length;
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsRiffWave(byte[] wav) =>
        wav.Length >= RiffPreambleSize &&
        wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F' &&
        wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E';

    /// <summary>
    /// Scans the RIFF chunk list and returns the raw PCM bytes of the "data" sub-chunk.
    /// </summary>
    private static byte[] ExtractPcmData(byte[] wav)
    {
        var offset = RiffPreambleSize;

        while (offset + 8 <= wav.Length)
        {
            // Keep chunk size as uint to avoid signed overflow
            var chunkSize = BitConverter.ToUInt32(wav, offset + 4);

            if (wav[offset] == 'd' && wav[offset + 1] == 'a' &&
                wav[offset + 2] == 't' && wav[offset + 3] == 'a')
            {
                var dataStart = offset + 8;
                var dataLen = (int)Math.Min(chunkSize, (uint)(wav.Length - dataStart));
                var pcm = new byte[dataLen];
                Buffer.BlockCopy(wav, dataStart, pcm, 0, dataLen);
                return pcm;
            }

            // Advance past this chunk; RIFF chunk sizes are rounded up to even boundaries
            var advance = 8u + chunkSize + (chunkSize % 2);
            if (advance == 0 || offset + advance > (uint)wav.Length) break; // guard against malformed data
            offset += (int)advance;
        }

        return [];
    }

    /// <summary>
    /// Builds a canonical 44-byte PCM WAV header, copying fmt parameters from
    /// <paramref name="sourceWav"/> and writing the supplied total data size.
    /// </summary>
    private static byte[] BuildMinimalHeader(byte[] sourceWav, int totalPcmBytes)
    {
        var fmtPayload = FindChunk(sourceWav, "fmt ");

        var header = new byte[44];

        // RIFF preamble
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)(totalPcmBytes + 36));
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';

        // fmt sub-chunk (16 bytes of standard WAVEFORMAT)
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BitConverter.TryWriteBytes(header.AsSpan(16), 16u);
        if (fmtPayload.Length >= 16)
            Buffer.BlockCopy(fmtPayload, 0, header, 20, 16);

        // data sub-chunk
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)totalPcmBytes);

        return header;
    }

    private static byte[] FindChunk(byte[] wav, string id)
    {
        var offset = RiffPreambleSize;
        while (offset + 8 <= wav.Length)
        {
            var chunkSize = BitConverter.ToUInt32(wav, offset + 4);

            if (wav[offset] == id[0] && wav[offset + 1] == id[1] &&
                wav[offset + 2] == id[2] && wav[offset + 3] == id[3])
            {
                var start = offset + 8;
                var len = (int)Math.Min(chunkSize, (uint)(wav.Length - start));
                var payload = new byte[len];
                Buffer.BlockCopy(wav, start, payload, 0, len);
                return payload;
            }

            var advance = 8u + chunkSize + (chunkSize % 2);
            if (advance == 0 || offset + advance > (uint)wav.Length) break;
            offset += (int)advance;
        }
        return [];
    }
}
