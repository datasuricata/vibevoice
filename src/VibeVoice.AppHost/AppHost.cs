using CommunityToolkit.Aspire.Hosting.Ollama;

var builder = DistributedApplication.CreateBuilder(args);

// Switch TTS backend: "kokoro" (local GPU container) or "azure" (Azure Speech cloud)
var ttsBackend = builder.Configuration["TtsBackend"] ?? "kokoro";

var ollama = builder.AddOllama("ollama")
    .WithDataVolume()
    .WithGPUSupport();

var gemma = ollama.AddModel("gemma3", "gemma3:1b-it-fp16");

var vibeVoice = builder.AddProject<Projects.VibeVoice>("vibevoice")
    .WithExternalHttpEndpoints()
    .WithReference(gemma)
    .WithEnvironment("TtsBackend", ttsBackend)
    .WaitFor(gemma);

if (ttsBackend == "kokoro")
{
    var kokoro = builder.AddContainer("kokoro", "ghcr.io/remsky/kokoro-fastapi-gpu")
        .WithHttpEndpoint(port: 8880, targetPort: 8880)
        .WithContainerRuntimeArgs("--gpus", "all")
        .WithVolume("kokoro-models", "/app/api/src/models");

    vibeVoice
        .WithReference(kokoro.GetEndpoint("http"))
        .WaitFor(kokoro);
}

builder.Build().Run();
