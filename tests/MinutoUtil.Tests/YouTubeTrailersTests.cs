using System.Text.Json.Nodes;
using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Tests;

/// <summary>Which YouTube search results are a game's official trailers (yt-dlp's --flat-playlist shape, 2026-10-29).</summary>
public sealed class YouTubeTrailersTests
{
    private const string Rockstar = "UC6VcWc1rAoWdBCM0JxrRQ3A";
    private const string PlayStation = "UC-2Y8dQb0S6DtpxNgAKoJKA";

    private static JsonObject Result(string id, string title, string channel, string channelId, bool verified, double duration) =>
        new() { ["id"] = id, ["title"] = title, ["channel"] = channel, ["channel_id"] = channelId, ["channel_is_verified"] = verified, ["duration"] = duration };

    private static List<string> Picked(string game, params JsonObject[] results) =>
        YouTubeTrailers.Pick(results, game, new MediaOptions()).Select(v => v.Id).ToList();

    [Fact]
    public void KeepsTheOfficialChannelsTrailersAndDropsAReupload()
    {
        var picked = Picked("Grand Theft Auto VI",
            Result("QdBZY2fkU-0", "Grand Theft Auto VI Trailer 1", "Rockstar Games", Rockstar, true, 91),
            Result("rz1B4B2yBZY", "Grand Theft Auto VI Launch Trailer", "Rockstar Games", Rockstar, true, 91),
            Result("tJbzMqJGH4k", "Grand Theft Auto VI: An Extended Look", "Rockstar Games", Rockstar, true, 1609),
            // PlayStation's copy of the launch trailer: same length, another channel.
            Result("Qqzj-j4qk0E", "Grand Theft Auto VI - Launch Trailer | PS5 Games", "PlayStation", PlayStation, true, 91),
            Result("VQRLujxTm3c", "Grand Theft Auto VI Trailer 2", "Rockstar Games", Rockstar, true, 167));

        // Same channel and length is two different trailers; titles saying "trailer" come before the extended look.
        Assert.Equal(["QdBZY2fkU-0", "rz1B4B2yBZY", "VQRLujxTm3c", "tJbzMqJGH4k"], picked);
    }

    [Fact]
    public void RefusesFanChannelsReactionsAndOtherGames()
    {
        var picked = Picked("Grand Theft Auto VI",
            // Not verified and not on the list, however official the title sounds.
            Result("aaaaaaaaaaa", "Grand Theft Auto VI Official Trailer", "GTA Fan 4K", "UCfan", false, 91),
            // Verified, but not an official channel and not named after the game.
            Result("bbbbbbbbbbb", "Grand Theft Auto VI Trailer 2", "Big Gaming News", "UCnews", true, 167),
            // Official channel, but about the trailer.
            Result("ccccccccccc", "Grand Theft Auto VI Trailer 2 Breakdown", "IGN", "UCKy1dAqELo0zrOtPkf0eTMw", true, 900),
            Result("ddddddddddd", "Grand Theft Auto VI Trailer 1 Reaction", "GameSpot Trailers", "UCUnRn1f78foyP26XGkRfWsA", true, 600),
            // Other games, one whose name starts with the same letters.
            Result("eeeeeeeeeee", "Grand Theft Auto V Trailer", "Rockstar Games", Rockstar, true, 120),
            Result("hhhhhhhhhhh", "Grand Theft Auto: Vice City - The Definitive Edition Trailer", "Rockstar Games", Rockstar, true, 130),
            // A bad id is never used.
            Result("../../etc", "Grand Theft Auto VI Trailer", "Rockstar Games", Rockstar, true, 120));

        Assert.Empty(picked);
    }

    [Fact]
    public void TakesAVerifiedChannelNamedAfterTheGame()
    {
        var picked = Picked("Cyberpunk 2077 (video game)",
            Result("fffffffffff", "Cyberpunk 2077 2014 Official Launch Trailer", "Cyberpunk 2077", "UC4zyoIAzmdsgpDZQfO1-lSA", true, 140),
            Result("ggggggggggg", "Cyberpunk 2077 Trailer", "Cyberpunk 2077 Fans", "UCfans", false, 140));

        Assert.Equal(["fffffffffff"], picked);
    }

    [Fact]
    public void StudiosComeBeforeTheGamesRegionalChannelsAndThePressAndSequelsAreDropped()
    {
        // Free Fire's search (October 2026): India's and Malaysia's channels came first; Garena Brasil is a studio channel.
        var picked = YouTubeTrailers.Pick([
            Result("iiiiiiiiiii", "FREE FIRE INDIA IS HERE!", "Free Fire India Official", "UCindia", true, 92),
            Result("jjjjjjjjjjj", "Free Fire Official Trailer | IGN", "IGN", "UCKy1dAqELo0zrOtPkf0eTMw", true, 70),
            Result("kkkkkkkkkkk", "Free Fire - Trailer Oficial", "Garena Free Fire Brasil", "UCr4ARxgElIO21GWfIraZezg", true, 55),
            Result("lllllllllll", "Sequel to Free Fire - First Look Trailer", "Garena Free Fire Brasil", "UCr4ARxgElIO21GWfIraZezg", true, 87),
            Result("mmmmmmmmmmm", "Free Fire 7 anos", "Garena Free Fire Brasil", "UCr4ARxgElIO21GWfIraZezg", true, 40),
            Result("nnnnnnnnnnn", "ANIME FREE FIRE - TRAILER | FREE FIRE BRASIL", "Garena Free Fire Brasil", "UCr4ARxgElIO21GWfIraZezg", true, 30)],
            "Free Fire (video game)", new MediaOptions());

        Assert.Equal(["kkkkkkkkkkk", "mmmmmmmmmmm", "iiiiiiiiiii", "jjjjjjjjjjj"], picked.Select(v => v.Id));
        // A word of the game's own name is no reason to drop its trailer.
        Assert.Single(YouTubeTrailers.Pick([Result("ooooooooooo", "Live A Live - Launch Trailer - Nintendo Switch", "Nintendo of America", "UCGIY_O-8vW4rfX98KlMkvRg", true, 90)],
            "Live A Live", new MediaOptions()));
    }

    [Theory]
    [InlineData("QdBZY2fkU-0", "QdBZY2fkU-0")]
    [InlineData("https://www.youtube.com/watch?v=QdBZY2fkU-0&t=10s", "QdBZY2fkU-0")]
    [InlineData("https://youtu.be/VQRLujxTm3c", "VQRLujxTm3c")]
    [InlineData("https://m.youtube.com/shorts/VQRLujxTm3c", "VQRLujxTm3c")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=QdBZY2fkU-0", null)]
    [InlineData("https://www.youtube.com/playlist?list=PL123", null)]
    [InlineData("--exec=calc", null)]
    [InlineData("QdBZY2fkU-0;rm", null)]
    public void ReadsAVideoIdFromAnIdOrALink(string text, string? id) => Assert.Equal(id, YouTubeTrailers.VideoId(text));

    /// <summary>
    /// The real thing, as a cloud run meets it: Steam has no GTA VI, so its trailer comes from Rockstar's YouTube channel.
    /// Uses the network and yt-dlp, so it runs only with MINUTO_LIVE_TESTS=1.
    /// </summary>
    [Fact]
    public async Task AGameSteamDoesNotSellGetsItsTrailerFromYouTube()
    {
        if (Environment.GetEnvironmentVariable("MINUTO_LIVE_TESTS") != "1") return;
        using var temp = new TempFolder();
        var options = new MediaOptions { Trailers = 1, MaxTrailerSeconds = 30 };
        var ffmpeg = new Ffmpeg(new VideoOptions());
        using var http = new HttpClient();
        var trailers = new GameTrailers(new SteamTrailers(http, ffmpeg, options),
            new YouTubeTrailers(ffmpeg, options, Path.Combine(TestPaths.RepositoryRoot, "tools")), options);

        var found = await trailers.GatherAsync("Grand Theft Auto VI", temp.Path, CancellationToken.None);

        var trailer = Assert.Single(found);
        Assert.Equal("YouTube", trailer.Source);
        Assert.Equal("Rockstar Games", trailer.Author);
        Assert.Equal(1080, trailer.Height);
        Assert.InRange(trailer.DurationSeconds!.Value, 29, 30.5);
        Assert.True(File.Exists(Path.Combine(temp.Path, "trailers.json")));
        Assert.False((await ffmpeg.ProbeAsync(trailer.LocalPath!, CancellationToken.None)).HasAudio);
    }
}
