using System.Text;
using Microsoft.Extensions.AI;

namespace VibeVoice.Services;

public enum PodcastGenerationStage { FetchingNews, NewsLoaded, GeneratingScript, Complete }

public record PodcastGenerationProgress(
    PodcastGenerationStage Stage,
    string Message,
    IReadOnlyList<NewsItem>? NewsItems = null,
    string? ScriptChunk = null);

public class PodcastGeneratorService(
    IChatClient chatClient,
    ITtsService tts,
    NewsScraperService newsService)
{
    private static string BuildSystemPrompt(string narratorName, string lang)
    {
        var isPtBr = lang == "pt-BR";
        var forbidden = isPtBr
            ? "PROIBIDO — nunca inclua:\n" +
              "- Rótulos de fala como \"Apresentador:\", \"Narrador:\", seu próprio nome seguido de dois pontos, ou qualquer label de personagem\n" +
              "- Descrições de cena entre parênteses como \"(Música de abertura)\" ou \"(Vinheta)\"\n" +
              "- Instruções de produção como \"[música]\", \"[pausa]\", \"[efeito sonoro]\"\n" +
              "- Markdown: asteriscos, hashtags, negrito, itálico, listas, colchetes\n" +
              "- Títulos de seção ou separadores"
            : "FORBIDDEN — never include:\n" +
              "- Speaker labels like \"Host:\", \"Narrator:\", your name followed by a colon, or any character label\n" +
              "- Scene descriptions in parentheses like \"(Opening music)\" or \"(Jingle)\"\n" +
              "- Production instructions like \"[music]\", \"[pause]\", \"[sound effect]\"\n" +
              "- Markdown: asterisks, hashtags, bold, italic, lists, brackets\n" +
              "- Section titles or separators";

        var required = isPtBr
            ? "OBRIGATÓRIO:\n" +
              $"- Apresente-se pelo nome no início: use \"{narratorName}\" naturalmente na fala (ex: \"Olá, eu sou {narratorName} e bem-vindos ao VibeVoice!\")\n" +
              "- Apenas texto corrido da fala, como uma transcrição literal do que você diz\n" +
              "- Português brasileiro claro e natural\n" +
              "- Frases curtas, no máximo 15 palavras cada\n" +
              "- Use vírgulas e reticências (...) para pausas naturais\n" +
              "- Números por extenso (\"dois mil e vinte e seis\", nunca \"2026\")\n" +
              $"- Termine com encerramento natural (ex: \"Aqui é {narratorName}. Até a próxima!\")"
            : "REQUIRED:\n" +
              $"- Introduce yourself by name at the start: use \"{narratorName}\" naturally (e.g. \"Hi, I'm {narratorName} and welcome to VibeVoice!\")\n" +
              "- Only the spoken text, as a literal transcript of what you say\n" +
              "- Clear, natural English\n" +
              "- Short sentences, max 15 words each\n" +
              "- Use commas and ellipses (...) for natural pauses\n" +
              "- Numbers spelled out (\"twenty twenty-six\", never \"2026\")\n" +
              $"- End with a natural closing (e.g. \"This is {narratorName}. See you next time!\")";

        var role = isPtBr
            ? $"Você é {narratorName}, um locutor de podcast brasileiro. Sua única tarefa é gerar o texto exato que você vai falar."
            : $"You are {narratorName}, a podcast host. Your only task is to generate the exact text you will speak.";

        return $"{role}\n\n{forbidden}\n\n{required}";
    }

    public async Task<string> GenerateScriptAsync(
        string topic,
        string narratorName,
        string language,
        IProgress<PodcastGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new(PodcastGenerationStage.FetchingNews,
            "Fetching latest news from Forbes..."));

        var newsItems = await newsService.GetLatestNewsAsync(maxItems: 12, ct: ct);

        progress?.Report(new(PodcastGenerationStage.NewsLoaded,
            $"{newsItems.Count} articles loaded.", newsItems));

        var isPtBr = language == "pt-BR";
        var newsLabel = isPtBr ? "Últimas notícias do Forbes:" : "Latest Forbes news:";
        var newsContext = newsItems.Count > 0
            ? $"\n\n{newsLabel}\n" +
              string.Join("\n", newsItems.Select((n, i) => $"{i + 1}. [{n.Category}] {n.Title}"))
            : string.Empty;

        var userPrompt = isPtBr
            ? $"Gere um roteiro de podcast sobre o tema: {topic}.{newsContext}\n\nUse as notícias acima como contexto e base para o episódio."
            : $"Generate a podcast script about the topic: {topic}.{newsContext}\n\nUse the news above as context and basis for the episode.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(narratorName, language)),
            new(ChatRole.User, userPrompt)
        };

        progress?.Report(new(PodcastGenerationStage.GeneratingScript,
            "Gemma 3 generating script..."));

        var scriptBuilder = new StringBuilder();

        await foreach (var chunk in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            var text = chunk.Text;
            if (!string.IsNullOrEmpty(text))
            {
                scriptBuilder.Append(text);
                progress?.Report(new(PodcastGenerationStage.GeneratingScript,
                    "Generating script...", ScriptChunk: text));
            }
        }

        progress?.Report(new(PodcastGenerationStage.Complete, "Script ready!"));
        return scriptBuilder.ToString();
    }

    public async Task<byte[]> GeneratePodcastAudioAsync(
        string script,
        string voice,
        CancellationToken ct = default)
    {
        var clean = ScriptSanitizer.Sanitize(script);
        return await tts.GenerateAudioAsync(clean, voice, ct);
    }

    /// <summary>
    /// Streams the script sentence-by-sentence from the LLM, synthesises each sentence
    /// immediately and yields (sentence, audioBytes) so the caller can play audio in real time.
    /// </summary>
    public async IAsyncEnumerable<(string Sentence, byte[] Audio)> GenerateStreamingPodcastAsync(
        string topic,
        string voice,
        string narratorName,
        string language,
        IProgress<PodcastGenerationProgress>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        progress?.Report(new(PodcastGenerationStage.FetchingNews, "Fetching latest news from Forbes..."));

        var newsItems = await newsService.GetLatestNewsAsync(maxItems: 12, ct: ct);

        progress?.Report(new(PodcastGenerationStage.NewsLoaded,
            $"{newsItems.Count} articles loaded.", newsItems));

        var isPtBr = language == "pt-BR";
        var newsLabel = isPtBr ? "Últimas notícias do Forbes:" : "Latest Forbes news:";
        var newsContext = newsItems.Count > 0
            ? $"\n\n{newsLabel}\n" +
              string.Join("\n", newsItems.Select((n, i) => $"{i + 1}. [{n.Category}] {n.Title}"))
            : string.Empty;

        var userPrompt = isPtBr
            ? $"Gere um roteiro de podcast sobre o tema: {topic}.{newsContext}\n\nUse as notícias acima como contexto e base para o episódio."
            : $"Generate a podcast script about the topic: {topic}.{newsContext}\n\nUse the news above as context and basis for the episode.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(narratorName, language)),
            new(ChatRole.User, userPrompt)
        };

        progress?.Report(new(PodcastGenerationStage.GeneratingScript, "Gemma 3 generating script..."));

        var buffer = new StringBuilder();

        await foreach (var chunk in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            var text = chunk.Text;
            if (string.IsNullOrEmpty(text)) continue;

            buffer.Append(text);
            progress?.Report(new(PodcastGenerationStage.GeneratingScript,
                "Generating script...", ScriptChunk: text));

            // Flush complete sentences (split on . ! ?)
            while (TryExtractSentence(buffer, out var sentence))
            {
                var clean = ScriptSanitizer.Sanitize(sentence);
                if (string.IsNullOrWhiteSpace(clean)) continue;
                var audio = await tts.GenerateAudioAsync(clean, voice, ct);
                yield return (clean, audio);
            }
        }

        // Flush any remaining text
        var remainder = ScriptSanitizer.Sanitize(buffer.ToString());
        if (!string.IsNullOrEmpty(remainder))
        {
            var audio = await tts.GenerateAudioAsync(remainder, voice, ct);
            yield return (remainder, audio);
        }

        progress?.Report(new(PodcastGenerationStage.Complete, "Script ready!"));
    }

    // Extracts the first complete sentence from the buffer (ends with . ! ?)
    private static bool TryExtractSentence(StringBuilder buffer, out string sentence)
    {
        var text = buffer.ToString();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?')
            {
                // Look ahead: sentence ends if followed by space, newline, or end-of-buffer
                if (i + 1 >= text.Length || text[i + 1] is ' ' or '\n' or '\r')
                {
                    sentence = text[..(i + 1)].Trim();
                    buffer.Remove(0, i + 1);
                    // Skip leading whitespace in buffer
                    while (buffer.Length > 0 && char.IsWhiteSpace(buffer[0]))
                        buffer.Remove(0, 1);
                    return true;
                }
            }
        }
        sentence = string.Empty;
        return false;
    }
}
