using System.Text;
using System.Text.RegularExpressions;

namespace VibeVoice.Services;

/// <summary>
/// Strips LLM formatting artefacts from a generated script so only
/// clean spoken text reaches the TTS engine.
/// </summary>
public static partial class ScriptSanitizer
{
    // ## Title or ### Section
    [GeneratedRegex(@"^\s*#{1,6}\s+.*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLine();

    // Lines that are *entirely* a stage direction: **(Vinheta)**, (Música...), etc.
    [GeneratedRegex(@"^\s*\*{0,2}\s*\([^)]+\)\s*\*{0,2}\s*$", RegexOptions.Multiline)]
    private static partial Regex StageDirectionLine();

    // Speaker label at line start: **Apresentador(a):** / Narrador: / Host:
    // Stops at the first colon; allows up to 60 chars (covers "Apresentador(a)")
    [GeneratedRegex(@"^\s*\*{0,2}[^:\n]{1,60}\*{0,2}\s*:\s*")]
    private static partial Regex SpeakerLabel();

    // Inline stage direction anywhere in a line: (Música...) or **(Vinheta)**
    [GeneratedRegex(@"\*{0,2}\s*\([^)]{0,120}\)\s*\*{0,2}")]
    private static partial Regex InlineStageDirection();

    // **bold** or *italic* — keeps inner text
    [GeneratedRegex(@"\*{1,2}([^*\n]*)\*{1,2}")]
    private static partial Regex MarkdownEmphasis();

    // __bold__ or _italic_
    [GeneratedRegex(@"_{1,2}([^_\n]*)_{1,2}")]
    private static partial Regex MarkdownUnderscoreEmphasis();

    // Multiple spaces / tabs collapsed to one
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 1. Remove header lines and pure stage-direction lines
        var text = HeaderLine().Replace(raw, string.Empty);
        text = StageDirectionLine().Replace(text, string.Empty);

        // 2. Process remaining lines individually
        var lines = text.Split('\n');
        var result = new StringBuilder();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var line = rawLine;

            // Remove speaker labels at line start
            line = SpeakerLabel().Replace(line, string.Empty);

            // Remove inline stage directions
            line = InlineStageDirection().Replace(line, " ");

            // Strip markdown emphasis — preserve inner text
            line = MarkdownEmphasis().Replace(line, "$1");
            line = MarkdownUnderscoreEmphasis().Replace(line, "$1");

            // Remove any leftover raw symbols
            line = line.Replace("**", "").Replace("*", "").Replace("_", "").Replace("#", "");

            // Collapse multiple spaces
            line = MultipleSpaces().Replace(line, " ").Trim();

            if (!string.IsNullOrWhiteSpace(line))
            {
                if (result.Length > 0) result.Append(' ');
                result.Append(line);
            }
        }

        return result.ToString().Trim();
    }
}
