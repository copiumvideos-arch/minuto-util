using System.Net;
using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Tests;

/// <summary>Finding a game's Steam app and its best trailer stream, with Steam's answers replaced (shapes from 2026-10-29).</summary>
public sealed class SteamTrailersTests
{
    private static SteamTrailers Steam(FakeHandler handler) => new(new HttpClient(handler), new Ffmpeg(new VideoOptions()), new MediaOptions());

    private static FakeHandler Search(string itemsJson) => new((request, _) => request.RequestUri!.AbsolutePath == "/api/storesearch/"
        ? FakeHandler.Json(HttpStatusCode.OK, $$"""{"total":3,"items":[{{itemsJson}}]}""")
        : new HttpResponseMessage(HttpStatusCode.NotFound));

    [Fact]
    public async Task PrefersTheExactNameOverASequelThatStartsWithIt()
    {
        var handler = Search("""{"type":"app","name":"Hades II","id":1145350},{"type":"app","name":"Hades","id":1145360}""");

        Assert.Equal(1145360, await Steam(handler).FindAppAsync("Hades (video game)", CancellationToken.None));
        // Wikipedia's disambiguation is not searched for.
        Assert.Contains("term=Hades&", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task TakesALaterEditionWhoseNameStartsWithTheGames()
    {
        var handler = Search("""{"type":"app","name":"The Witcher 3: Wild Hunt â€” Remastered","id":292030},{"type":"app","name":"The Witcher 3: Wild Hunt â€” Songs of the Past","id":5006530}""");

        Assert.Equal(292030, await Steam(handler).FindAppAsync("The Witcher 3: Wild Hunt", CancellationToken.None));
    }

    [Fact]
    public async Task ANameThatOnlyStartsWithTheGamesLettersIsAnotherGame()
    {
        // Steam's real answer for "Grand Theft Auto VI" on 2026-10-29: "vi" is where "Vice City" starts.
        var handler = Search("""{"type":"app","name":"Grand Theft Auto: Vice City – The Definitive Edition","id":1546990},{"type":"app","name":"Grand Theft Auto V Enhanced","id":3240220}""");

        await Assert.ThrowsAsync<MediaException>(() => Steam(handler).FindAppAsync("Grand Theft Auto VI", CancellationToken.None));
    }

    [Fact]
    public async Task RefusesToGuessWhenNoResultIsTheGame()
    {
        var handler = Search("""{"type":"app","name":"Witcher Hunter Simulator","id":1},{"type":"bundle","name":"The Witcher 3: Wild Hunt","id":2}""");

        var error = await Assert.ThrowsAsync<MediaException>(() => Steam(handler).FindAppAsync("The Witcher 3: Wild Hunt", CancellationToken.None));
        Assert.Contains("--steam-app", error.Message);
    }

    [Fact]
    public async Task PicksTheLargestStreamUpTo1080FromTheMasterPlaylist()
    {
        const string master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Default",URI="hls_264_4_audio.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=9000000,RESOLUTION=3840x2160
            hls_264_9_video.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2600000,RESOLUTION=1280x720
            hls_264_1_video.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5800000,RESOLUTION=1920x1080
            hls_264_0_video.m3u8
            """;
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(master) });

        var stream = await Steam(handler).BestStreamAsync("https://video.akamai.steamstatic.com/store_trailers/292030/1/abc/1/hls_264_master.m3u8?t=1", CancellationToken.None);

        Assert.Equal("https://video.akamai.steamstatic.com/store_trailers/292030/1/abc/1/hls_264_0_video.m3u8", stream.ToString());
    }

    [Theory]
    [InlineData("http://video.akamai.steamstatic.com/a/hls_264_master.m3u8")]
    [InlineData("https://steamstatic.com.example.net/a/hls_264_master.m3u8")]
    [InlineData("file:///etc/passwd")]
    public async Task FollowsOnlyHttpsStreamsOnSteamsVideoHost(string url)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("nothing may be fetched"));

        await Assert.ThrowsAsync<MediaException>(() => Steam(handler).BestStreamAsync(url, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AStreamInThePlaylistOnAnotherHostIsRefused()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=1920x1080\nhttps://evil.example.com/video.m3u8\n";
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(master) });

        await Assert.ThrowsAsync<MediaException>(() => Steam(handler).BestStreamAsync("https://video.akamai.steamstatic.com/a/hls_264_master.m3u8", CancellationToken.None));
    }

    [Fact]
    public void PutsBackNamesSteamSendsAsWindows1252AndDropsDashes() =>
        Assert.Equal("The Witcher 3: Wild Hunt - Remastered | Official Launch Trailer",
            SteamTrailers.Clean("The Witcher 3: Wild Hunt â€” Remastered | Official Launch Trailer"));
}
