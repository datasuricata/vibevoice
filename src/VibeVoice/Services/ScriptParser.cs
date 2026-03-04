namespace VibeVoice.Services;

/// <summary>
/// Parses a dialogue script into per-speaker segments.
/// Expected format per line: "SpeakerName: text content"
/// </summary>
public static class ScriptParser
{
    /// <summary>
    /// Splits a dialogue script into (speakerId, text) segments where
    /// speakerId is "host1" or "host2" based on the narrator names provided.
    /// Lines that cannot be attributed to a known speaker are assigned to
    /// the last known speaker (default: host1).
    /// </summary>
    public static IEnumerable<(string SpeakerId, string Text)> ParseSpeakerSegments(
        string script, string name1, string name2)
    {
        if (string.IsNullOrWhiteSpace(script))
            yield break;

        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var currentSpeaker = "host1";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var (speakerId, text) = ParseLineLabel(line, name1, name2, currentSpeaker);
            currentSpeaker = speakerId;

            if (!string.IsNullOrWhiteSpace(text))
                yield return (speakerId, text);
        }
    }

    /// <summary>
    /// Extracts the speaker identity and text from a single line.
    /// Returns the default speaker if no known label is found at the line start.
    /// </summary>
    public static (string SpeakerId, string Text) ParseLineLabel(
        string line, string name1, string name2, string defaultSpeaker = "host1")
    {
        if (string.IsNullOrWhiteSpace(line))
            return (defaultSpeaker, line);

        var colonIdx = line.IndexOf(':');
        if (colonIdx > 0 && colonIdx <= 60)
        {
            // Strip bold markers around label (e.g. **Bruno**)
            var label = line[..colonIdx].Trim().Trim('*').Trim();
            var text = line[(colonIdx + 1)..].Trim();

            if (!string.IsNullOrEmpty(text))
            {
                if (NamesMatch(label, name1)) return ("host1", text);
                if (NamesMatch(label, name2)) return ("host2", text);
            }
        }

        // No recognizable label — treat whole line as continuation of current speaker
        return (defaultSpeaker, line);
    }

    private static bool NamesMatch(string label, string name) =>
        label.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        label.Contains(name, StringComparison.OrdinalIgnoreCase);
}
