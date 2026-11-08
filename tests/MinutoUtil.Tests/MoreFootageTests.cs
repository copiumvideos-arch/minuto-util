using MinutoUtil.Core;
using MinutoUtil.Core.Media;

namespace MinutoUtil.Tests;

/// <summary>
/// Trailers from every source (Steam, the Microsoft Store, YouTube) and no spare writer for Profissão Spoiler: the
/// Shrek 2 Remastered video of 2026-11-07 had one 49-second Steam trailer for 48 seconds of narration, and the ROB RUN
/// script of 2026-11-08 came from a spare writer.
/// </summary>
public sealed class MoreFootageTests
{
    // The video tracks of the Shrek 2 Remastered trailer's manifest on the Microsoft Store (2026-11-08), then its audio.
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
          <AdaptationSet mimeType="video/mp4">
            <Representation id="0" bandwidth="250000" width="320" height="180" codecs="avc1.4D400D"/>
            <Representation id="1" bandwidth="700000" width="640" height="360" codecs="avc1.4D401E"/>
            <Representation id="4" bandwidth="3308108" width="1280" height="720" codecs="avc1.4D401F"/>
            <Representation id="5" bandwidth="4574225" width="1920" height="1080" codecs="avc1.4D4028"/>
            <Representation id="6" bandwidth="5902890" width="1920" height="1080" codecs="avc1.4D4028"/>
            <Representation id="7" bandwidth="9000000" width="3840" height="2160" codecs="avc1.4D4033"/>
          </AdaptationSet>
          <AdaptationSet mimeType="audio/mp4"><Representation id="0" bandwidth="44100" codecs="mp4a.40.2"/></AdaptationSet>
        </Period></MPD>
        """;

    [Fact]
    public void The_best_track_up_to_1080p_is_picked_from_a_store_manifest() =>
        Assert.Equal(4, MicrosoftStoreTrailers.BestVideoTrack(Manifest));

    [Fact]
    public void A_manifest_with_a_dtd_is_refused() =>
        Assert.ThrowsAny<System.Xml.XmlException>(() => MicrosoftStoreTrailers.BestVideoTrack(
            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><MPD>&e;</MPD>"""));

    [Theory]
    [InlineData("https://cdn.trailers.xboxservices.com/trailers/x/manifest.mpd", true)]
    [InlineData("http://cdn.trailers.xboxservices.com/trailers/x/manifest.mpd", false)]
    [InlineData("https://evil.example.com/manifest.mpd", false)]
    [InlineData("https://cdn.trailers.xboxservices.com.evil.com/manifest.mpd", false)]
    [InlineData("https://cdn.trailers.xboxservices.com/trailers/x/video.mp4", false)]
    public void Only_https_manifests_on_microsofts_trailer_host_are_followed(string url, bool followed) =>
        Assert.Equal(followed, MicrosoftStoreTrailers.Manifest(url) is not null);

    [Theory]
    [InlineData("Shrek 2 Remastered", "DreamWorks Shrek 2 Remastered", false)]
    [InlineData("DreamWorks Shrek 2 Remastered", "DreamWorks Shrek 2 Remastered", true)]
    [InlineData("Hades II", "Hades", false)]
    [InlineData("The Witcher 3: Wild Hunt - Complete Edition", "The Witcher 3: Wild Hunt", true)]
    [InlineData("Grand Theft Auto: Vice City", "Grand Theft Auto VI", false)]
    public void A_store_product_is_the_game_only_by_its_name_or_an_edition(string product, string game, bool same) =>
        Assert.Equal(same, SteamTrailers.SameGame(product, game));

    [Fact]
    public void A_youtube_video_of_a_trailer_already_had_from_a_store_is_left_out()
    {
        var had = new List<MediaItem>
        {
            new()
            {
                Id = "steam-1", Kind = MediaKind.Video, Source = "Steam", Title = "Shrek 2 Announce Trailer", Author = "Aspyr",
                License = "Trailer oficial", SourceUrl = "https://store.steampowered.com/app/2523740/", DownloadUrl = "x", DurationSeconds = 49.0,
            },
        };
        var videos = new List<YouTubeTrailers.Video>
        {
            new("aaaaaaaaaaa", "Shrek 2 Announce Trailer", "Aspyr", "c", true, 80),
            new("bbbbbbbbbbb", "Shrek 2 Remastered - Official Trailer", "Aspyr", "c", true, 49.6),
            new("ccccccccccc", "Shrek 2 Remastered - Gameplay Trailer", "Aspyr", "c", true, 95),
        };
        Assert.Equal(["ccccccccccc"], YouTubeTrailers.WithoutTrailersHad(videos, had).Select(v => v.Id));
    }

    [Fact]
    public void Profissao_spoiler_never_uses_a_spare_writer_and_minuto_util_still_does()
    {
        Assert.False(Channel.Load(TestPaths.RepositoryRoot, "profissao-spoiler").Config.Writer.SpareWriters);
        Assert.True(Channel.Load(TestPaths.RepositoryRoot, "minuto-util").Config.Writer.SpareWriters);
    }
}
