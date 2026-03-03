namespace VibeVoice.Services;

public record VoiceOption(string Id, string DisplayName, string Group, string NarratorName, string Language);

public interface ITtsService
{
    string BackendName { get; }
    string DefaultVoice { get; }
    IReadOnlyList<VoiceOption> AvailableVoices { get; }
    Task<byte[]> GenerateAudioAsync(string text, string voice, CancellationToken ct = default);
}
