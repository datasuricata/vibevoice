using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VibeVoice.Services;

public class KokoroTtsService(HttpClient httpClient, ILogger<KokoroTtsService> logger) : ITtsService
{
    public string BackendName => "Kokoro";
    public string DefaultVoice => "pm_santa";

    public IReadOnlyList<VoiceOption> AvailableVoices { get; } =
    [
        new("pm_santa",   "pm_santa — PT-BR Male (default)",   "Brazilian Portuguese", "Bruno", "pt-BR"),
        new("pf_dora",    "pf_dora — PT-BR Female",            "Brazilian Portuguese", "Dora", "pt-BR"),
        new("pm_alex",    "pm_alex — PT-BR Male",              "Brazilian Portuguese", "Alex", "pt-BR"),
        new("af_heart",   "af_heart — American English Female", "English",              "Heart", "en"),
        new("af_bella",   "af_bella — American English Female", "English",              "Bella", "en"),
        new("am_adam",    "am_adam — American English Male",    "English",              "Adam", "en"),
        new("am_michael", "am_michael — American English Male", "English",              "Michael", "en"),
    ];

    public async Task<byte[]> GenerateAudioAsync(
        string text,
        string voice,
        CancellationToken ct = default)
    {
        var request = new KokoroSpeechRequest(
            Model: "kokoro",
            Input: text,
            Voice: voice,
            ResponseFormat: "wav",
            Speed: 1.0f);

        logger.LogInformation("Kokoro TTS: voice={Voice}, chars={Chars}", voice, text.Length);

        var response = await httpClient.PostAsJsonAsync("/v1/audio/speech", request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

file record KokoroSpeechRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("voice")] string Voice,
    [property: JsonPropertyName("response_format")] string ResponseFormat,
    [property: JsonPropertyName("speed")] float Speed);
