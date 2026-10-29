using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Tests;

/// <summary>The footage releases the owner's PC fills for the cloud, and trailers.json read back in another place.</summary>
public sealed class FootageCacheTests
{
    [Theory]
    [InlineData("Grand Theft Auto VI", "footage-grand-theft-auto-vi")]
    [InlineData("Hades (video game)", "footage-hades")]
    [InlineData("The Witcher 3: Wild Hunt", "footage-the-witcher-3-wild-hunt")]
    [InlineData("Pokémon Legends: Z-A", "footage-pokemon-legends-z-a")]
    public void NamesTheReleaseAfterTheGame(string game, string tag) => Assert.Equal(tag, FootageCache.ReleaseTag(game));

    [Fact]
    public async Task TrailersSavedOnThePcAreFoundNextToTheManifestInTheCloud()
    {
        using var temp = new TempFolder();
        var media = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(Path.Combine(media, "trailers"));
        await File.WriteAllTextAsync(Path.Combine(media, "trailers", "youtube-QdBZY2fkU-0.mp4"), "video");
        var saved = new MediaItem
        {
            Id = "youtube-QdBZY2fkU-0", Kind = MediaKind.Video, Source = "YouTube", Title = "Grand Theft Auto VI Trailer 1",
            Author = "Rockstar Games", License = "Trailer oficial", SourceUrl = "https://www.youtube.com/watch?v=QdBZY2fkU-0",
            DownloadUrl = "https://www.youtube.com/watch?v=QdBZY2fkU-0",
            LocalPath = @"D:\projects\minuto-util\output\footage\footage-grand-theft-auto-vi\media\trailers\youtube-QdBZY2fkU-0.mp4",
        };
        await File.WriteAllTextAsync(Path.Combine(media, "trailers.json"), JsonSerializer.Serialize(new[] { saved }, Json.Options));
        var options = new MediaOptions();
        var ffmpeg = new Ffmpeg(new VideoOptions());
        // Any network call would fail the test: the saved list must be enough.
        var offline = new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("nothing may be fetched")));
        var trailers = new GameTrailers(new SteamTrailers(offline, ffmpeg, options), new YouTubeTrailers(ffmpeg, options, temp.Path), options);

        var found = await trailers.GatherAsync("Grand Theft Auto VI", media, CancellationToken.None);

        Assert.Equal(Path.Combine(media, "trailers", "youtube-QdBZY2fkU-0.mp4"), Assert.Single(found).LocalPath);
    }
}
