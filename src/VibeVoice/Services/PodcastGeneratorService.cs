using System.Text;
using Microsoft.Extensions.AI;

namespace VibeVoice.Services;

public enum PodcastGenerationStage { FetchingNews, NewsLoaded, GeneratingScript, Complete }

public record PodcastGenerationProgress(
    PodcastGenerationStage Stage,
    string Message,
    IReadOnlyList<NewsItem>? NewsItems = null,
    string? ScriptChunk = null);

/// <summary>
/// Carries all user choices for a single podcast generation.
/// Set <see cref="Voice2"/> and <see cref="NarratorName2"/> to enable dialogue mode.
/// </summary>
public record PodcastConfig(
    string Topic,
    string Language,
    string Voice1,
    string NarratorName1,
    string? Voice2 = null,
    string? NarratorName2 = null)
{
    /// <summary>True when two hosts have been selected.</summary>
    public bool IsDialogue => Voice2 is not null && NarratorName2 is not null;
}

public class PodcastGeneratorService(
    IChatClient chatClient,
    ITtsService tts,
    NewsScraperService newsService)
{
    // ── Prompt builders ───────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string narratorName, string lang)
    {
        var isPtBr = lang == "pt-BR";
        var forbidden = isPtBr
            ? "PROIBIDO — nunca inclua:\n" +
              "- Rótulos de fala como \"Apresentador:\", \"Narrador:\", seu próprio nome seguido de dois pontos, ou qualquer label de personagem\n" +
              "- Descrições de cena entre parênteses como \"(Música de abertura)\" ou \"(Vinheta)\"\n" +
              "- Instruções de produção como \"[música]\", \"[pausa]\", \"[efeito sonoro]\"\n" +
              "- Markdown: asteriscos, hashtags, negrito, itálico, listas, colchetes\n" +
              "- Títulos de seção ou separadores\n" +
              "- Símbolos: evite %, $, @, &, #, números digitais, abreviações como \"ex.\", \"p.ex.\", \"etc.\" — use sempre por extenso"
            : "FORBIDDEN — never include:\n" +
              "- Speaker labels like \"Host:\", \"Narrator:\", your name followed by a colon, or any character label\n" +
              "- Scene descriptions in parentheses like \"(Opening music)\" or \"(Jingle)\"\n" +
              "- Production instructions like \"[music]\", \"[pause]\", \"[sound effect]\"\n" +
              "- Markdown: asterisks, hashtags, bold, italic, lists, brackets\n" +
              "- Section titles or separators\n" +
              "- Symbols: avoid %, $, @, &, #, digit numbers, abbreviations like \"e.g.\", \"etc.\" — always spell out";

        var required = isPtBr
            ? "OBRIGATÓRIO:\n" +
              $"- Apresente-se pelo nome no início: use \"{narratorName}\" naturalmente na fala (exemplo: \"Olá, eu sou {narratorName} e bem-vindos ao VibeVoice!\")\n" +
              "- Apenas texto corrido da fala, como uma transcrição literal do que você diz\n" +
              "- Português brasileiro claro e natural\n" +
              "- Frases curtas, no máximo 15 palavras cada\n" +
              "- Use vírgulas e reticências (...) para pausas naturais\n" +
              "- SEMPRE números por extenso: \"dois mil e vinte e seis\", \"vinte e cinco por cento\", \"três milhões\" — nunca dígitos ou símbolos\n" +
              "- Narrativa por extenso: use \"por exemplo\", \"porcentagem\", \"e assim por diante\" em vez de abreviações ou símbolos\n" +
              $"- Termine com encerramento natural (exemplo: \"Aqui é {narratorName}. Até a próxima!\")"
            : "REQUIRED:\n" +
              $"- Introduce yourself by name at the start: use \"{narratorName}\" naturally (example: \"Hi, I'm {narratorName} and welcome to VibeVoice!\")\n" +
              "- Only the spoken text, as a literal transcript of what you say\n" +
              "- Clear, natural English\n" +
              "- Short sentences, max 15 words each\n" +
              "- Use commas and ellipses (...) for natural pauses\n" +
              "- ALWAYS spell out numbers: \"twenty twenty-six\", \"twenty-five percent\", \"three million\" — never digits or symbols\n" +
              "- Narrative in full: use \"for example\", \"percent\", \"and so on\" instead of abbreviations or symbols\n" +
              $"- End with a natural closing (example: \"This is {narratorName}. See you next time!\")";

        var role = isPtBr
            ? $"Você é {narratorName}, um locutor de podcast brasileiro. Sua única tarefa é gerar o texto exato que você vai falar."
            : $"You are {narratorName}, a podcast host. Your only task is to generate the exact text you will speak.";

        return $"{role}\n\n{forbidden}\n\n{required}";
    }

    private static string BuildDialogSystemPrompt(string name1, string name2, string lang)
    {
        var isPtBr = lang == "pt-BR";

        var format = isPtBr
            ? $"FORMATO OBRIGATÓRIO — cada fala deve estar em sua própria linha, iniciando EXATAMENTE com o nome do locutor seguido de dois pontos:\n" +
              $"{name1}: texto da fala\n" +
              $"{name2}: texto da resposta\n" +
              $"{name1}: continuação...\n" +
              "Nunca use placeholders como \"[Nome do Podcast]\", \"[seu nome]\", \"[data]\"; use sempre o conteúdo real.\n"
            : $"REQUIRED FORMAT — each line must start EXACTLY with the speaker's name followed by a colon:\n" +
              $"{name1}: spoken text\n" +
              $"{name2}: response text\n" +
              $"{name1}: continuation...\n" +
              "Never use placeholders like \"[Podcast Name]\", \"[your name]\", \"[date]\"; always use real content.\n";

        var forbidden = isPtBr
            ? "PROIBIDO — nunca inclua:\n" +
              "- Placeholders entre colchetes como \"[Nome do Podcast]\", \"[data]\", \"[assunto]\" — use sempre o valor real\n" +
              "- Descrições de cena entre parênteses como \"(Música de abertura)\" ou \"(Vinheta)\"\n" +
              "- Instruções de produção como \"[música]\", \"[pausa]\", \"[efeito sonoro]\"\n" +
              "- Markdown: asteriscos, hashtags, negrito, itálico, listas\n" +
              "- Títulos de seção ou separadores\n" +
              "- Símbolos: evite %, $, @, &, #, números digitais, abreviações como \"ex.\", \"p.ex.\", \"etc.\" — use sempre por extenso"
            : "FORBIDDEN — never include:\n" +
              "- Bracket placeholders like \"[Podcast Name]\", \"[date]\", \"[topic]\" — always use the real value\n" +
              "- Scene descriptions in parentheses like \"(Opening music)\" or \"(Jingle)\"\n" +
              "- Production instructions like \"[music]\", \"[pause]\", \"[sound effect]\"\n" +
              "- Markdown: asterisks, hashtags, bold, italic, lists\n" +
              "- Section titles or separators\n" +
              "- Symbols: avoid %, $, @, &, #, digit numbers, abbreviations like \"e.g.\", \"etc.\" — always spell out";

        var required = isPtBr
            ? "OBRIGATÓRIO:\n" +
              $"- O nome do podcast é VibeVoice — use-o naturalmente, nunca um placeholder\n" +
              $"- Abertura obrigatória: {name1} se apresenta e menciona VibeVoice; {name2} também se apresenta na sequência\n" +
              $"  Exemplo: \"{name1}: Olá, eu sou {name1} e este é o VibeVoice!\" seguido de \"{name2}: E eu sou {name2}, bem-vindos!\"\n" +
              $"- Gere um diálogo natural e envolvente entre {name1} e {name2}\n" +
              $"- {name1} conduz a conversa; {name2} comenta, questiona e complementa\n" +
              "- Cada locutor fala em frases curtas, no máximo 15 palavras por fala\n" +
              "- Os locutores se alternam com frequência, criando uma conversa dinâmica\n" +
              "- De vez em quando, insira uma sacada irônica ou bem-humorada de um dos locutores — " +
              "uma observação sarcástica leve, uma brincadeira ou um comentário divertido sobre a notícia — " +
              "para tornar a conversa mais natural e menos robótica\n" +
              "- SEMPRE números por extenso: \"dois mil e vinte e seis\", \"vinte e cinco por cento\"\n" +
              $"- Encerramento: ambos se despedem mencionando VibeVoice (exemplo: \"{name1}: Isso é tudo por hoje no VibeVoice!\")"
            : "REQUIRED:\n" +
              $"- The podcast name is VibeVoice — use it naturally, never a placeholder\n" +
              $"- Opening: {name1} introduces themselves and mentions VibeVoice; {name2} also introduces themselves\n" +
              $"  Example: \"{name1}: Hi, I'm {name1} and this is VibeVoice!\" followed by \"{name2}: And I'm {name2}, welcome!\"\n" +
              $"- Generate a natural, engaging dialogue between {name1} and {name2}\n" +
              $"- {name1} leads the conversation; {name2} comments, questions and adds insight\n" +
              "- Each host speaks in short sentences, max 15 words per turn\n" +
              "- Hosts alternate frequently, creating a dynamic back-and-forth\n" +
              "- Occasionally, one host drops a witty or ironic remark — a light sarcastic comment, " +
              "a playful joke, or a funny observation about the news — to make the conversation feel " +
              "natural and not robotic\n" +
              "- ALWAYS spell out numbers: \"twenty twenty-six\", \"twenty-five percent\"\n" +
              $"- Closing: both say goodbye mentioning VibeVoice (example: \"{name1}: That's all for today on VibeVoice!\")";

        var role = isPtBr
            ? $"Você é o roteirista do podcast VibeVoice, com dois locutores: {name1} e {name2}. " +
              "Sua tarefa é gerar o diálogo completo exatamente como será falado, sem nenhum placeholder."
            : $"You are the scriptwriter for the podcast VibeVoice, with two hosts: {name1} and {name2}. " +
              "Your task is to generate the complete dialogue exactly as it will be spoken, with no placeholders.";

        return $"{role}\n\n{format}\n{forbidden}\n\n{required}";
    }

    // ── News fetching + prompt helpers ────────────────────────────────────────

    private static string BuildNewsContext(IReadOnlyList<NewsItem> newsItems, string lang)
    {
        if (newsItems.Count == 0) return string.Empty;
        var label = lang == "pt-BR" ? "Últimas notícias do Forbes:" : "Latest Forbes news:";
        return $"\n\n{label}\n" +
               string.Join("\n", newsItems.Select((n, i) => $"{i + 1}. [{n.Category}] {n.Title}"));
    }

    private static string BuildUserPrompt(string topic, string newsContext, string lang) =>
        lang == "pt-BR"
            ? $"Gere um roteiro de podcast sobre o tema: {topic}.{newsContext}\n\nUse as notícias acima como contexto e base para o episódio."
            : $"Generate a podcast script about the topic: {topic}.{newsContext}\n\nUse the news above as context and basis for the episode.";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a podcast script using the LLM.
    /// Dialogue mode is activated automatically when <see cref="PodcastConfig.IsDialogue"/> is true.
    /// </summary>
    public async Task<string> GenerateScriptAsync(
        PodcastConfig config,
        IProgress<PodcastGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new(PodcastGenerationStage.FetchingNews,
            "Fetching latest news from Forbes..."));

        var newsItems = await newsService.GetLatestNewsAsync(maxItems: 12, ct: ct);

        progress?.Report(new(PodcastGenerationStage.NewsLoaded,
            $"{newsItems.Count} articles loaded.", newsItems));

        var systemPrompt = config.IsDialogue
            ? BuildDialogSystemPrompt(config.NarratorName1, config.NarratorName2!, config.Language)
            : BuildSystemPrompt(config.NarratorName1, config.Language);

        var newsContext = BuildNewsContext(newsItems, config.Language);
        var userPrompt = BuildUserPrompt(config.Topic, newsContext, config.Language);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
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

    /// <summary>
    /// Synthesises a complete script into a single WAV byte array.
    /// In dialogue mode each speaker segment is synthesised with the correct voice
    /// and the resulting WAVs are concatenated.
    /// </summary>
    public async Task<byte[]> GeneratePodcastAudioAsync(
        string script,
        PodcastConfig config,
        CancellationToken ct = default)
    {
        if (!config.IsDialogue)
        {
            var clean = ScriptSanitizer.Sanitize(script);
            return await tts.GenerateAudioAsync(clean, config.Voice1, ct);
        }

        var segments = ScriptParser
            .ParseSpeakerSegments(script, config.NarratorName1, config.NarratorName2!)
            .ToList();

        var wavChunks = new List<byte[]>(segments.Count);

        foreach (var (speakerId, text) in segments)
        {
            var clean = ScriptSanitizer.Sanitize(text);
            if (string.IsNullOrWhiteSpace(clean)) continue;
            var voice = speakerId == "host1" ? config.Voice1 : config.Voice2!;
            wavChunks.Add(await tts.GenerateAudioAsync(clean, voice, ct));
        }

        return WavHelper.Concatenate(wavChunks);
    }

    /// <summary>
    /// Streams the podcast sentence-by-sentence, yielding (sentence, audioBytes, speakerId)
    /// so the caller can play audio in real time.
    /// speakerId is "host1" or "host2" (always "host1" in solo mode).
    /// </summary>
    public async IAsyncEnumerable<(string Sentence, byte[] Audio, string SpeakerId)> GenerateStreamingPodcastAsync(
        PodcastConfig config,
        IProgress<PodcastGenerationProgress>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        progress?.Report(new(PodcastGenerationStage.FetchingNews, "Fetching latest news from Forbes..."));

        var newsItems = await newsService.GetLatestNewsAsync(maxItems: 12, ct: ct);

        progress?.Report(new(PodcastGenerationStage.NewsLoaded,
            $"{newsItems.Count} articles loaded.", newsItems));

        var systemPrompt = config.IsDialogue
            ? BuildDialogSystemPrompt(config.NarratorName1, config.NarratorName2!, config.Language)
            : BuildSystemPrompt(config.NarratorName1, config.Language);

        var newsContext = BuildNewsContext(newsItems, config.Language);
        var userPrompt = BuildUserPrompt(config.Topic, newsContext, config.Language);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        progress?.Report(new(PodcastGenerationStage.GeneratingScript, "Gemma 3 generating script..."));

        var buffer = new StringBuilder();
        var currentSpeakerId = "host1";

        await foreach (var chunk in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            var text = chunk.Text;
            if (string.IsNullOrEmpty(text)) continue;

            buffer.Append(text);
            progress?.Report(new(PodcastGenerationStage.GeneratingScript,
                "Generating script...", ScriptChunk: text));

            if (config.IsDialogue)
            {
                while (TryExtractLine(buffer, out var line))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var (speakerId, lineText) = ScriptParser.ParseLineLabel(
                        line, config.NarratorName1, config.NarratorName2!, currentSpeakerId);
                    currentSpeakerId = speakerId;

                    await foreach (var item in SynthesiseSentencesAsync(
                        lineText, speakerId, config.Voice1, config.Voice2!, ct))
                        yield return item;
                }
            }
            else
            {
                while (TryExtractSentence(buffer, out var sentence))
                {
                    var clean = ScriptSanitizer.Sanitize(sentence);
                    if (string.IsNullOrWhiteSpace(clean)) continue;
                    var audio = await tts.GenerateAudioAsync(clean, config.Voice1, ct);
                    yield return (clean, audio, "host1");
                }
            }
        }

        // Flush remaining buffer
        if (config.IsDialogue)
        {
            var remaining = buffer.ToString().Trim();
            if (!string.IsNullOrEmpty(remaining))
            {
                var (speakerId, lineText) = ScriptParser.ParseLineLabel(
                    remaining, config.NarratorName1, config.NarratorName2!, currentSpeakerId);
                await foreach (var item in SynthesiseSentencesAsync(
                    lineText, speakerId, config.Voice1, config.Voice2!, ct))
                    yield return item;
            }
        }
        else
        {
            var remainder = ScriptSanitizer.Sanitize(buffer.ToString());
            if (!string.IsNullOrEmpty(remainder))
            {
                var audio = await tts.GenerateAudioAsync(remainder, config.Voice1, ct);
                yield return (remainder, audio, "host1");
            }
        }

        progress?.Report(new(PodcastGenerationStage.Complete, "Script ready!"));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Splits lineText into sentences and synthesises each with the appropriate voice.
    /// </summary>
    private async IAsyncEnumerable<(string Sentence, byte[] Audio, string SpeakerId)> SynthesiseSentencesAsync(
        string lineText,
        string speakerId,
        string voice1,
        string voice2,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var selectedVoice = speakerId == "host1" ? voice1 : voice2;
        var sentBuf = new StringBuilder(lineText);

        while (TryExtractSentence(sentBuf, out var sentence))
        {
            var clean = ScriptSanitizer.Sanitize(sentence);
            if (string.IsNullOrWhiteSpace(clean)) continue;
            var audio = await tts.GenerateAudioAsync(clean, selectedVoice, ct);
            yield return (clean, audio, speakerId);
        }

        // Flush any sentence fragment at end of line
        var remainder = ScriptSanitizer.Sanitize(sentBuf.ToString());
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            var audio = await tts.GenerateAudioAsync(remainder, selectedVoice, ct);
            yield return (remainder, audio, speakerId);
        }
    }

    /// <summary>
    /// Extracts the first newline-terminated line from the buffer, stripping the newline.
    /// </summary>
    private static bool TryExtractLine(StringBuilder buffer, out string line)
    {
        var text = buffer.ToString();
        var idx = text.IndexOf('\n');
        if (idx < 0)
        {
            line = string.Empty;
            return false;
        }
        line = text[..idx].Trim();
        buffer.Remove(0, idx + 1);
        return true;
    }

    /// <summary>
    /// Extracts the first complete sentence from the buffer (ends with . ! ?).
    /// </summary>
    private static bool TryExtractSentence(StringBuilder buffer, out string sentence)
    {
        var text = buffer.ToString();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?')
            {
                // Sentence ends if followed by space, newline, or end-of-buffer
                if (i + 1 >= text.Length || text[i + 1] is ' ' or '\n' or '\r')
                {
                    sentence = text[..(i + 1)].Trim();
                    buffer.Remove(0, i + 1);
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
