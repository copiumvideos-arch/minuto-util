using System.Net;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

/// <summary>
/// The voice reads straight through colons, so a section is spoken in pieces there and at a "|" and joined with set
/// silences. Checked on the real voice.py join, with Smallest.ai answering generated tones.
/// </summary>
public sealed class SpeechPiecesTests
{
    [Fact]
    public void SplitsAfterAColonAndAtAMarkButNotInsideATime()
    {
        var pieces = SpeechPieces.Split("E o pior: a ideia foi dele, | às 11:30.", 0.45, 0.3);

        Assert.Equal([new SpeechPiece("E o pior:", 0.45), new SpeechPiece("a ideia foi dele,", 0.3), new SpeechPiece("às 11:30.", 0)], pieces);
        Assert.Equal([new SpeechPiece("Sem pausa nenhuma.", 0)], SpeechPieces.Split("Sem pausa nenhuma.", 0.45, 0.3));
        Assert.Equal([new SpeechPiece("Fim.", 0)], SpeechPieces.Split("| Fim. |", 0.45, 0.3));
    }

    [Fact]
    public async Task SpeaksASectionInPiecesAndJoinsThemWithTheSetPause()
    {
        using var temp = new TempFolder();
        var tone = Path.Combine(temp.Path, "tone.wav");
        await Generated.ToneAsync(tone, 1);
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(tone)) });
        var usage = new RunUsage();
        var options = new VoiceOptions { Listener = "none" };
        var tools = Path.Combine(TestPaths.RepositoryRoot, "tools");
        var narrator = new Narrator(options, tools, new SmallestVoice(new HttpClient(handler), "k", options.Smallest, usage), new KokoroVoice(tools, options), usage);
        var script = new VideoScript("Título", "Em 2015 saiu o jogo.", "", "",
            [new ScriptSection("E o pior: | a ideia foi dele.", ["q"], ["q"], "")], "Descrição.", ["games"]);

        var timings = await narrator.NarrateAsync(script, temp.Path, CancellationToken.None);

        // The hook in one piece keeps its old name (cached takes stay valid); the section is two pieces (a mark right
        // after a colon is the same break).
        var texts = handler.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Em dois mil e quinze saiu o jogo.", "E o pior:", "a ideia foi dele."], texts);
        var smallest = Path.Combine(temp.Path, "voice", "smallest");
        foreach (var name in new[] { "section-00", "section-01-0", "section-01-1" })
            Assert.True(File.Exists(Path.Combine(smallest, $"{name}.wav")), name);
        // The captions show the script without the mark.
        Assert.Equal(["E", "o", "pior:", "a", "ideia", "foi", "dele."], timings.Words.Where(w => w.Section == 1).Select(w => w.Text));
        // Two one-second tones with the colon's 0.45 s between them, plus the trimmed edges.
        var section = timings.Sections[1];
        Assert.InRange(section.End - section.Start, 2 + 0.45, 2 + 0.45 + 0.3);
    }

    [Fact]
    public async Task TheVoiceGetsRespelledWordsWhileTheCaptionsKeepTheScript()
    {
        // The GTA VI video: the voice ran "GTA" together; Brazilians say each letter.
        using var temp = new TempFolder();
        var tone = Path.Combine(temp.Path, "tone.wav");
        await Generated.ToneAsync(tone, 1);
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(tone)) });
        var usage = new RunUsage();
        var options = new VoiceOptions { Listener = "none", Pronunciations = new() { ["GTA"] = "gê tê á" } };
        var tools = Path.Combine(TestPaths.RepositoryRoot, "tools");
        var narrator = new Narrator(options, tools, new SmallestVoice(new HttpClient(handler), "k", options.Smallest, usage), new KokoroVoice(tools, options), usage);
        var script = new VideoScript("GTA 6", "O GTA seis custou caro.", "", "",
            [new ScriptSection("Fãs de GTAs antigos esperaram.", ["q"], ["q"], "")], "Descrição.", ["games"]);

        var timings = await narrator.NarrateAsync(script, temp.Path, CancellationToken.None);

        var texts = handler.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("text").GetString()).ToList();
        // Whole words only: "GTAs" is left alone.
        Assert.Equal(["O gê tê á seis custou caro.", "Fãs de GTAs antigos esperaram."], texts);
        Assert.Equal(["O", "GTA", "seis", "custou", "caro."], timings.Words.Where(w => w.Section == 0).Select(w => w.Text));
    }
}
