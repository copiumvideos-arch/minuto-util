using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Voice;

/// <summary>One piece of a section the voice speaks on its own, and the silence that follows it.</summary>
public sealed record SpeechPiece(string Text, double PauseAfter);

/// <summary>
/// Smallest.ai's voice reads straight through a colon and sometimes runs a comma together, so a section is spoken in
/// pieces wherever it needs a real pause: after a colon followed by a space ("E o pior: a ideia foi dele.") and at a
/// "|" the owner put in a script by hand. The pieces are joined with set silences by tools/voice.py. A colon inside a
/// time ("11:30") is not a break, and "|" never reaches the captions.
/// </summary>
public static partial class SpeechPieces
{
    public static IReadOnlyList<SpeechPiece> Split(string text, double colonPause, double markPause)
    {
        var pieces = new List<SpeechPiece>();
        var last = 0;
        foreach (Match m in Break().Matches(text))
        {
            Add(text[last..m.Index], m.Value.Contains('|') ? markPause : colonPause);
            last = m.Index + m.Length;
        }
        Add(text[last..], 0);
        if (pieces.Count == 0) return [new SpeechPiece(text.Trim(), 0)];
        pieces[^1] = pieces[^1] with { PauseAfter = 0 };
        return pieces;

        void Add(string piece, double pause)
        {
            piece = piece.Trim();
            if (piece.Length > 0) pieces.Add(new SpeechPiece(piece, pause));
        }
    }

    /// <summary>The text as the captions show it: without the "|" marks.</summary>
    public static string WithoutMarks(string text) => text.Replace('|', ' ');

    [GeneratedRegex(@"(?<=:)\s+|\s*\|\s*")]
    private static partial Regex Break();
}
