using System.Text;

namespace VibeVoice.Services;

public class AzureSpeechTtsService(
    HttpClient httpClient,
    IConfiguration config,
    ILogger<AzureSpeechTtsService> logger) : ITtsService
{
    private readonly string _key =
        config["AzureSpeech:Key"] ?? throw new InvalidOperationException("AzureSpeech:Key not configured");

    public string BackendName => "Azure Speech";
    public string DefaultVoice => "pt-BR-FranciscaNeural";

    public IReadOnlyList<VoiceOption> AvailableVoices { get; } =
    [
        new("pt-BR-FranciscaNeural", "Francisca — PT-BR Female", "Brazilian Portuguese", "Francisca", "pt-BR"),
        new("pt-BR-ThalitaNeural",   "Thalita — PT-BR Female",   "Brazilian Portuguese", "Thalita", "pt-BR"),
        new("pt-BR-AntonioNeural",   "Antônio — PT-BR Male",     "Brazilian Portuguese", "Antônio", "pt-BR"),
        new("pt-BR-HumbertoNeural",  "Humberto — PT-BR Male",    "Brazilian Portuguese", "Humberto", "pt-BR"),
        new("pt-BR-DonatoNeural",    "Donato — PT-BR Male",      "Brazilian Portuguese", "Donato", "pt-BR"),
        new("pt-BR-BrendaNeural",    "Brenda — PT-BR Female",    "Brazilian Portuguese", "Brenda", "pt-BR"),
        new("en-US-JennyNeural",     "Jenny — US English Female", "English",             "Jenny", "en"),
        new("en-US-GuyNeural",       "Guy — US English Male",     "English",             "Guy", "en"),
        new("en-US-AriaNeural",      "Aria — US English Female", "English",             "Aria", "en"),
        new("en-US-DavisNeural",     "Davis — US English Male",   "English",             "Davis", "en"),
    ];

    public async Task<byte[]> GenerateAudioAsync(
        string text,
        string voice,
        CancellationToken ct = default)
    {
        var lang = voice.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? "en-US" : "pt-BR";
        var ssml = $"""
            <speak version='1.0' xml:lang='{lang}'>
                <voice name='{voice}'>
                    {XmlEscape(text)}
                </voice>
            </speak>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "cognitiveservices/v1");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        logger.LogInformation("Azure Speech TTS: voice={Voice}, chars={Chars}", voice, text.Length);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static string XmlEscape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
