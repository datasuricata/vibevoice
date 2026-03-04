namespace VibeVoice.Services;

/// <summary>
/// Utilities for working with raw WAV (PCM) byte arrays.
/// Assumes standard RIFF/WAVE format with a 44-byte header.
/// </summary>
public static class WavHelper
{
    // Standard PCM WAV header is exactly 44 bytes:
    // [0-3]  "RIFF"
    // [4-7]  RIFF chunk size  = fileSize - 8
    // [8-11] "WAVE"
    // [12-15] "fmt "
    // [16-19] fmt chunk size  = 16
    // [20-21] audio format    = 1 (PCM)
    // [22-23] num channels
    // [24-27] sample rate
    // [28-31] byte rate
    // [32-33] block align
    // [34-35] bits per sample
    // [36-39] "data"
    // [40-43] data chunk size = PCM byte count
    private const int HeaderSize = 44;

    /// <summary>
    /// Concatenates multiple WAV buffers into a single WAV buffer.
    /// All inputs must share the same sample rate, channels, and bit depth.
    /// </summary>
    public static byte[] Concatenate(IReadOnlyList<byte[]> wavFiles)
    {
        var valid = wavFiles.Where(f => f is { Length: > HeaderSize }).ToList();
        if (valid.Count == 0) return [];
        if (valid.Count == 1) return valid[0];

        // Borrow header from first file
        var header = new byte[HeaderSize];
        Buffer.BlockCopy(valid[0], 0, header, 0, HeaderSize);

        // Collect raw PCM data from all files
        var pcmChunks = valid.Select(f => f.AsSpan(HeaderSize).ToArray()).ToList();
        var totalPcm = pcmChunks.Sum(c => c.Length);

        var result = new byte[HeaderSize + totalPcm];
        Buffer.BlockCopy(header, 0, result, 0, HeaderSize);

        // Patch RIFF chunk size  [4..7]
        BitConverter.TryWriteBytes(result.AsSpan(4), (uint)(result.Length - 8));
        // Patch data chunk size  [40..43]
        BitConverter.TryWriteBytes(result.AsSpan(40), (uint)totalPcm);

        var offset = HeaderSize;
        foreach (var chunk in pcmChunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
