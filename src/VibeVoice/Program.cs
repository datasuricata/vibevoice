using MudBlazor.Services;
using VibeVoice.Components;
using VibeVoice.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Feature flag: "azure" → Azure Speech cloud | "kokoro" (default) → local GPU container
var ttsBackend = builder.Configuration["TtsBackend"] ?? "kokoro";

if (ttsBackend == "azure")
{
    var region = builder.Configuration["AzureSpeech:Region"] ?? "eastus";

    builder.Services.AddHttpClient<ITtsService, AzureSpeechTtsService>(client =>
    {
        client.BaseAddress = new Uri($"https://{region}.tts.speech.microsoft.com/");
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.Add("User-Agent", "VibeVoice/1.0");
    });
}
else
{
    var kokoroUrl = builder.Configuration.GetConnectionString("kokoro") ?? "http://localhost:8880";

    builder.Services.AddHttpClient<ITtsService, KokoroTtsService>(client =>
    {
        client.BaseAddress = new Uri(kokoroUrl);
        client.Timeout = TimeSpan.FromMinutes(5);
    });
}

builder.AddOllamaApiClient("gemma3").AddChatClient();

builder.Services.AddScoped<PodcastGeneratorService>();

// Typed HttpClient for Forbes news scraping (external HTTP only)
builder.Services.AddHttpClient<NewsScraperService>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept-Language", "pt-BR,pt;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// REST endpoints for external testing (Aspire dashboard, curl, etc.)
app.MapPost("/api/tts", async (TtsRequest req, ITtsService tts, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text is required" });
    var voice = req.Voice ?? tts.DefaultVoice;
    var audio = await tts.GenerateAudioAsync(req.Text, voice, ct);
    return Results.File(audio, "audio/wav", "speech.wav");
});

app.MapPost("/api/podcast", async (PodcastRequest req, PodcastGeneratorService svc, ITtsService tts, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Topic)) return Results.BadRequest(new { error = "Topic is required" });
    var voice = req.Voice ?? tts.DefaultVoice;
    var language = req.Language ?? "pt-BR";
    var narratorName = tts.AvailableVoices.FirstOrDefault(v => v.Id == voice)?.NarratorName ?? voice;
    var config = new PodcastConfig(req.Topic, language, voice, narratorName);
    var script = await svc.GenerateScriptAsync(config, progress: null, ct);
    var audio = await svc.GeneratePodcastAudioAsync(script, config, ct);
    return Results.File(audio, "audio/wav", "podcast.wav");
});

app.MapGet("/api/tts/status", async (ITtsService tts, CancellationToken ct) =>
{
    if (tts is KokoroTtsService kokoro)
    {
        var healthy = await kokoro.IsHealthyAsync(ct);
        return Results.Ok(new { backend = tts.BackendName, status = healthy ? "ready" : "unavailable" });
    }
    return Results.Ok(new { backend = tts.BackendName, status = "ready" });
});

app.MapGet("/api/news", async (NewsScraperService scraper, CancellationToken ct) =>
    Results.Ok(await scraper.GetLatestNewsAsync(ct: ct)));

app.Run();

record TtsRequest(string Text, string? Voice);
record PodcastRequest(string Topic, string? Voice, string? Language);
