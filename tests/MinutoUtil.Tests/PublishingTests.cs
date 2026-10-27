using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MinutoUtil.Core;
using MinutoUtil.Core.Publishing;

namespace MinutoUtil.Tests;

/// <summary>Builds metadata.json content like a real run writes it (Windows line ends, a credits list).</summary>
internal static class PublishData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.FromHours(-3));

    public static MediaCredit Credit(int i, string license) =>
        new($"Picture {i} of the landing craft", "U.S. Army Signal Corps", license, "Wikimedia Commons",
            $"https://commons.wikimedia.org/wiki/File:Picture_{i}_of_the_landing_craft_{new string('x', 40)}.jpg", "image", $"media/p{i}.jpg");

    public static VideoMetadata Metadata(string title = "Como a Previsão do Tempo Mudou o Dia D", IReadOnlyList<MediaCredit>? media = null)
    {
        media ??= [Credit(1, "Public domain"), Credit(2, "CC BY-SA 4.0")];
        var description = new StringBuilder("A decisão de invadir a Normandia dependia do tempo. <b>Fatos</b>.\r\n\r\n");
        description.Append("Fonte: Wikipedia. Texto sob CC BY-SA 4.0.\r\n\r\nCréditos:\r\n");
        foreach (var m in media) description.Append($"- {m.Title}. {m.Author}. {m.License}. {m.Source}: {m.SourceUrl}\r\n");
        description.Append("\r\n#segundaguerra #diad #normandia #historia");
        return new VideoMetadata(title, description.ToString(), ["segundaguerra", "diad", "normandia", "historia"], 61.4,
            "smallest:thiago", "gemini", media, ["https://pt.wikipedia.org/wiki/Desembarques_na_Normandia"]);
    }

    /// <summary>A run folder with metadata.json and a stand-in video.mp4 of <paramref name="videoBytes"/> random bytes.</summary>
    public static string RunFolder(TempFolder temp, int videoBytes = 1000)
    {
        File.WriteAllText(Path.Combine(temp.Path, "metadata.json"), JsonSerializer.Serialize(Metadata(), Json.Options));
        var bytes = new byte[videoBytes];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(temp.Path, "video.mp4"), bytes);
        return temp.Path;
    }

    public static YouTubeOptions YouTubeConfig => AppConfig.Load(Path.Combine(TestPaths.RepositoryRoot, "config", "minuto-util.json")).YouTube;
}

public sealed class PublishCopyTests
{
    [Fact]
    public void YouTube_metadata_json_follows_the_channel_config()
    {
        var details = PublishCopy.YouTube(PublishData.Metadata(), PublishData.YouTubeConfig);

        var json = JsonDocument.Parse(YouTubeUploader.MetadataJson(details)).RootElement;
        var snippet = json.GetProperty("snippet");
        var status = json.GetProperty("status");
        Assert.Equal("Como a Previsão do Tempo Mudou o Dia D #shorts", snippet.GetProperty("title").GetString());
        Assert.Equal("27", snippet.GetProperty("categoryId").GetString());
        Assert.Equal("pt-BR", snippet.GetProperty("defaultLanguage").GetString());
        Assert.Equal("pt-BR", snippet.GetProperty("defaultAudioLanguage").GetString());
        Assert.Equal(["segundaguerra", "diad", "normandia", "historia"], snippet.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("public", status.GetProperty("privacyStatus").GetString());
        Assert.False(status.GetProperty("selfDeclaredMadeForKids").GetBoolean());
        Assert.False(status.GetProperty("containsSyntheticMedia").GetBoolean());

        var description = snippet.GetProperty("description").GetString()!;
        Assert.DoesNotContain("\r", description);
        Assert.DoesNotContain("<", description);
        Assert.DoesNotContain(">", description);
        Assert.StartsWith("A decisão de invadir a Normandia dependia do tempo. bFatos/b.\n\nFonte:", description);
    }

    [Fact]
    public void Synthetic_media_flag_is_configurable()
    {
        var details = PublishCopy.YouTube(PublishData.Metadata(), new YouTubeOptions { ContainsSyntheticMedia = true });

        Assert.True(JsonDocument.Parse(YouTubeUploader.MetadataJson(details)).RootElement.GetProperty("status").GetProperty("containsSyntheticMedia").GetBoolean());
    }

    [Fact]
    public void Shorts_tag_is_added_only_when_it_fits()
    {
        Assert.Equal("Dia D #shorts", PublishCopy.Title("Dia D", appendShortsTag: true));
        Assert.Equal("Dia D", PublishCopy.Title("Dia D", appendShortsTag: false));

        var ninetyTwo = new string('a', 92);
        Assert.Equal(ninetyTwo + " #shorts", PublishCopy.Title(ninetyTwo, true));
        var ninetyThree = new string('a', 93);
        Assert.Equal(ninetyThree, PublishCopy.Title(ninetyThree, true));
    }

    [Fact]
    public void Long_titles_are_cut_at_a_word_within_100_characters()
    {
        var words = string.Join(' ', Enumerable.Repeat("Normandia", 15)); // 149 characters
        var title = PublishCopy.Title(words, appendShortsTag: true);

        Assert.True(title.Length <= 100);
        Assert.EndsWith("Normandia", title);
        Assert.DoesNotContain("#shorts", title);

        Assert.Equal(100, PublishCopy.Title(new string('x', 140), true).Length);
    }

    [Fact]
    public void Description_under_the_limit_keeps_every_credit()
    {
        var description = PublishCopy.Description(PublishData.Metadata());

        Assert.Contains("Picture 1 of the landing craft", description);
        Assert.Contains("Picture 2 of the landing craft", description);
    }

    [Fact]
    public void Over_5000_bytes_only_public_domain_credits_are_folded()
    {
        var media = Enumerable.Range(1, 40).Select(i => PublishData.Credit(i, i % 5 == 0 ? "CC BY 4.0" : i % 2 == 0 ? "CC0" : "Public domain")).ToList();
        var metadata = PublishData.Metadata(media: media);
        Assert.True(Encoding.UTF8.GetByteCount(metadata.Description) > 5000);

        var description = PublishCopy.Description(metadata);

        Assert.True(Encoding.UTF8.GetByteCount(description) <= PublishCopy.MaxDescriptionBytes);
        foreach (var i in new[] { 5, 10, 15, 20, 25, 30, 35, 40 })
            Assert.Contains($"Picture {i} of the landing craft", description);
        Assert.Matches(@"- Mais \d+ itens de domínio público\.", description);
        Assert.Single(description.Split('\n'), l => l.StartsWith("- Mais ", StringComparison.Ordinal));
        Assert.EndsWith("#segundaguerra #diad #normandia #historia", description);
    }

    [Fact]
    public void Attribution_credits_are_never_dropped_to_fit()
    {
        var media = Enumerable.Range(1, 40).Select(i => PublishData.Credit(i, "CC BY-SA 4.0")).ToList();

        Assert.Throws<InvalidDataException>(() => PublishCopy.Description(PublishData.Metadata(media: media)));
    }

    [Fact]
    public void Tiktok_caption_is_title_summary_and_hashtags_within_the_limit()
    {
        Assert.Equal("Como a Previsão do Tempo Mudou o Dia D\n\nA decisão de invadir a Normandia dependia do tempo. bFatos/b.\n\n" +
            "#segundaguerra #diad #normandia #historia #curiosidades #aprendanotiktok",
            PublishCopy.TikTokCaption(PublishData.Metadata(), new TikTokOptions()));

        // Up to 4 of the video's own tags, then the discovery tags; accents and spaces go; no repeats.
        var metadata = PublishData.Metadata() with { Hashtags = ["#História do Brasil", "Lei Áurea", "historia", "abolição", "1888", "brasil"] };
        Assert.EndsWith("\n\n#historiadobrasil #leiaurea #historia #abolicao #curiosidades #aprendanotiktok", PublishCopy.TikTokCaption(metadata, new TikTokOptions()));

        var caption = PublishCopy.TikTokCaption(PublishData.Metadata(new string('t', 3000)), new TikTokOptions());
        Assert.Equal(2200, caption.Length);
        Assert.DoesNotContain("Normandia dependia", caption);
        Assert.EndsWith("#segundaguerra #diad #normandia #historia #curiosidades #aprendanotiktok", caption);

        var tight = PublishCopy.TikTokCaption(PublishData.Metadata(), new TikTokOptions { MaxCaptionChars = 160 });
        Assert.True(tight.Length <= 160);
        Assert.EndsWith("#aprendanotiktok", tight);
    }
}

public sealed class YouTubeUploaderTests
{
    private static VideoDetails Details => new("Title", "Description", ["historia"], "27", "public", false, false, "pt-BR", "pt-BR");

    [Fact]
    public async Task Session_carries_metadata_and_the_file_goes_up_in_chunks()
    {
        using var temp = new TempFolder();
        var file = TempFile(temp, YouTubeUploader.ChunkSize + 100);
        var youtube = new FakeYouTube();
        var uploader = new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("token-1"), TimeSpan.Zero);

        var video = await uploader.UploadAsync(file, Details, CancellationToken.None);

        Assert.Equal("vid123", video.VideoId);
        Assert.Equal("https://youtube.com/shorts/vid123", video.Url);
        var start = youtube.Requests[0];
        Assert.Equal(HttpMethod.Post, start.Method);
        Assert.Equal("Bearer token-1", start.Authorization);
        Assert.Equal("public", JsonDocument.Parse(start.Body!).RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());
        var total = YouTubeUploader.ChunkSize + 100;
        Assert.Equal([$"bytes 0-{YouTubeUploader.ChunkSize - 1}/{total}", $"bytes {YouTubeUploader.ChunkSize}-{total - 1}/{total}"],
            youtube.Requests.Skip(1).Select(r => r.ContentRange));
        Assert.Equal(File.ReadAllBytes(file), youtube.Received.ToArray());
    }

    [Fact]
    public async Task An_interrupted_chunk_resumes_from_what_youtube_already_has()
    {
        using var temp = new TempFolder();
        var file = TempFile(temp, YouTubeUploader.ChunkSize + 100);
        var youtube = new FakeYouTube { FailNextChunkWith = HttpStatusCode.ServiceUnavailable, KeepPartOfFailedChunk = 1024 };
        var uploader = new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero);

        await uploader.UploadAsync(file, Details, CancellationToken.None);

        Assert.Contains(youtube.Requests, r => r.ContentRange == $"bytes */{YouTubeUploader.ChunkSize + 100}");
        Assert.Contains(youtube.Requests, r => r.ContentRange?.StartsWith("bytes 1024-", StringComparison.Ordinal) == true);
        Assert.Equal(File.ReadAllBytes(file), youtube.Received.ToArray());
    }

    [Fact]
    public async Task Quota_errors_say_to_retry_later()
    {
        using var temp = new TempFolder();
        var youtube = new FakeYouTube { StartStatus = HttpStatusCode.Forbidden, StartBody = """{"error":{"errors":[{"reason":"quotaExceeded"}]}}""" };
        var uploader = new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<YouTubeUploadException>(() => uploader.UploadAsync(TempFile(temp, 10), Details, CancellationToken.None));

        Assert.True(ex.RetryLater);
        Assert.Contains("quota", ex.Message);
    }

    [Fact]
    public async Task A_rejected_upload_does_not_retry()
    {
        using var temp = new TempFolder();
        var youtube = new FakeYouTube { StartStatus = HttpStatusCode.BadRequest, StartBody = """{"error":{"errors":[{"reason":"invalidTitle"}]}}""" };
        var uploader = new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<YouTubeUploadException>(() => uploader.UploadAsync(TempFile(temp, 10), Details, CancellationToken.None));

        Assert.False(ex.RetryLater);
        Assert.Single(youtube.Requests);
    }

    [Fact]
    public async Task Only_the_configured_channel_is_accepted()
    {
        var minuto = new FakeYouTube { ChannelId = "UCNath7BzjeX3ehJCA7cNYAA", ChannelTitle = "Minuto Útil" };
        Assert.Equal("Minuto Útil", await YouTubeUploader.EnsureChannelAsync(new HttpClient(minuto), "t", "UCNath7BzjeX3ehJCA7cNYAA", CancellationToken.None));
        Assert.Equal("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", minuto.Requests.Single().Url);

        var flopometro = new FakeYouTube { ChannelId = "UCflopometro", ChannelTitle = "Flopômetro" };
        var ex = await Assert.ThrowsAsync<YouTubeUploadException>(() =>
            YouTubeUploader.EnsureChannelAsync(new HttpClient(flopometro), "t", "UCNath7BzjeX3ehJCA7cNYAA", CancellationToken.None));
        Assert.Contains("UCflopometro", ex.Message);

        var none = new FakeYouTube { ChannelId = null };
        await Assert.ThrowsAsync<YouTubeUploadException>(() =>
            YouTubeUploader.EnsureChannelAsync(new HttpClient(none), "t", "UCNath7BzjeX3ehJCA7cNYAA", CancellationToken.None));
    }

    private static string TempFile(TempFolder temp, int size)
    {
        var path = Path.Combine(temp.Path, $"video-{size}.mp4");
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

internal sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? ContentRange, string? ContentType, long Length, string? Body);

/// <summary>Behaves like YouTube's channels endpoint and resumable upload, including partial receipt of a failed chunk.</summary>
internal sealed class FakeYouTube : HttpMessageHandler
{
    private const string Session = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=abc";

    public List<SeenRequest> Requests { get; } = [];
    public MemoryStream Received { get; } = new();
    public string? ChannelId { get; init; } = "UCNath7BzjeX3ehJCA7cNYAA";
    public string ChannelTitle { get; init; } = "Minuto Útil";
    public HttpStatusCode StartStatus { get; init; } = HttpStatusCode.OK;
    public string StartBody { get; init; } = "";
    public HttpStatusCode? FailNextChunkWith { get; set; }
    public int KeepPartOfFailedChunk { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var range = request.Content?.Headers.TryGetValues("Content-Range", out var values) == true ? values.First() : null;
        Requests.Add(new SeenRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), range,
            request.Content?.Headers.ContentType?.MediaType, body.Length, request.Method == HttpMethod.Post ? Encoding.UTF8.GetString(body) : null));

        if (request.Method == HttpMethod.Get)
        {
            var items = ChannelId is null ? "[]" : $$$"""[{"id":"{{{ChannelId}}}","snippet":{"title":"{{{ChannelTitle}}}"}}]""";
            return FakeHandler.Json(HttpStatusCode.OK, $$$"""{"items":{{{items}}}}""");
        }
        if (request.Method == HttpMethod.Post)
        {
            var start = new HttpResponseMessage(StartStatus) { Content = new StringContent(StartBody) };
            if (StartStatus == HttpStatusCode.OK) start.Headers.Location = new Uri(Session);
            return start;
        }

        var total = long.Parse(range!.Split('/')[1]);
        if (range.StartsWith("bytes */", StringComparison.Ordinal)) return Incomplete();

        if (FailNextChunkWith is { } failure)
        {
            FailNextChunkWith = null;
            Received.Write(body, 0, KeepPartOfFailedChunk);
            return new HttpResponseMessage(failure);
        }

        var offset = long.Parse(range["bytes ".Length..range.IndexOf('-')]);
        Assert.Equal(Received.Length, offset);
        Received.Write(body);
        return Received.Length >= total
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"vid123","status":{"privacyStatus":"public"}}""") }
            : Incomplete();
    }

    private HttpResponseMessage Incomplete()
    {
        var response = new HttpResponseMessage((HttpStatusCode)308);
        if (Received.Length > 0) response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received.Length - 1}");
        return response;
    }
}

public sealed class GoogleAuthTests
{
    [Fact]
    public void Consent_url_asks_only_for_upload_and_read_only_access_with_pkce()
    {
        var url = GoogleAuth.AuthorizationUrl("client", "http://127.0.0.1:5000/", "challenge", "state");

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal(["https://www.googleapis.com/auth/youtube.upload", "https://www.googleapis.com/auth/youtube.readonly"], query["scope"]!.Split(' '));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.DoesNotContain(' ', url.AbsoluteUri);
    }

    [Fact]
    public async Task Browser_sign_in_checks_the_channel_then_stores_the_token_and_refreshes_silently()
    {
        var google = new FakeGoogle();
        var store = new MemoryTokenStore();
        var time = new FakeTimeProvider(PublishData.Now);
        var auth = new GoogleAuth(new HttpClient(google), new OAuthClient("client-id", "client-secret"), store, time);
        string? verifiedWith = null;

        await auth.AuthorizeInteractiveAsync(PlayBrowser("auth-code"), (token, _) =>
        {
            verifiedWith = token;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal("access-1", verifiedWith);
        Assert.Equal("refresh-1", store.Token);
        var exchange = google.Forms.Single();
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("auth-code", exchange["code"]);
        Assert.True(exchange["code_verifier"].Length >= 43);

        Assert.Equal("access-1", await auth.GetAccessTokenAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("access-2", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("refresh-1", google.Forms[^1]["refresh_token"]);
    }

    [Fact]
    public async Task A_sign_in_with_the_wrong_channel_is_not_saved()
    {
        var store = new MemoryTokenStore();
        var auth = new GoogleAuth(new HttpClient(new FakeGoogle()), new OAuthClient("c", "s"), store, new FakeTimeProvider(PublishData.Now));
        var flopometro = new HttpClient(new FakeYouTube { ChannelId = "UCflopometro", ChannelTitle = "Flopômetro" });

        await Assert.ThrowsAsync<YouTubeUploadException>(() => auth.AuthorizeInteractiveAsync(PlayBrowser("auth-code"),
            (token, ct) => YouTubeUploader.EnsureChannelAsync(flopometro, token, "UCNath7BzjeX3ehJCA7cNYAA", ct), CancellationToken.None));

        Assert.Null(store.Token);
    }

    [Fact]
    public async Task A_redirect_with_the_wrong_state_is_ignored()
    {
        var google = new FakeGoogle();
        var auth = new GoogleAuth(new HttpClient(google), new OAuthClient("c", "s"), new MemoryTokenStore(), new FakeTimeProvider(PublishData.Now));
        string? forgedResponse = null;

        await auth.AuthorizeInteractiveAsync(url =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                forgedResponse = await browser.GetStringAsync($"{query["redirect_uri"]}?state=forged&code=evil");
                await browser.GetStringAsync($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"]!)}&code=auth-code");
            });
        }, (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Contains("not the one Minuto", forgedResponse);
        Assert.Equal("auth-code", google.Forms.Single()["code"]);
    }

    [Fact]
    public async Task An_unanswered_sign_in_link_gives_a_clear_message()
    {
        var auth = new GoogleAuth(new HttpClient(new FakeGoogle()), new OAuthClient("id", "secret"), new MemoryTokenStore(), TimeProvider.System)
        {
            SignInWait = TimeSpan.FromMilliseconds(200),
        };

        var ex = await Assert.ThrowsAsync<PublishAuthException>(() => auth.AuthorizeInteractiveAsync(_ => { }, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("youtube-login", ex.Message);
    }

    [Fact]
    public async Task Revoked_sign_in_says_to_log_in_again()
    {
        var auth = new GoogleAuth(new HttpClient(new FakeGoogle { Fail = true }), new OAuthClient("c", "s"), new MemoryTokenStore { Token = "old" },
            new FakeTimeProvider(PublishData.Now));

        var ex = await Assert.ThrowsAsync<PublishAuthException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("youtube-login", ex.Message);
    }

    [Fact]
    public void Client_file_must_be_a_desktop_app()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "client_secret.json");
        File.WriteAllText(path, """{"web":{"client_id":"x","client_secret":"y"}}""");

        Assert.Throws<PublishAuthException>(() => OAuthClient.Load(path));
        File.WriteAllText(path, """{"installed":{"client_id":"x","client_secret":"y"}}""");
        Assert.Equal(new OAuthClient("x", "y"), OAuthClient.Load(path));
    }

    /// <summary>Plays the browser: follows the consent URL's redirect back to the local listener.</summary>
    internal static Action<Uri> PlayBrowser(string code) => url =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        _ = Task.Run(async () =>
        {
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"]!)}&code={code}");
        });
    };

    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];
        public bool Fail { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(GoogleAuth.TokenEndpoint, request.RequestUri);
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            Forms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            if (Fail) return FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            var refresh = form["grant_type"] == "authorization_code" ? ""","refresh_token":"refresh-1" """ : "";
            return FakeHandler.Json(HttpStatusCode.OK, $$$"""{"access_token":"access-{{{Forms.Count}}}","expires_in":3599{{{refresh}}}}""");
        }
    }
}

internal sealed class MemoryTokenStore : ITokenStore
{
    public string? Token { get; set; }
    public string? Load() => Token;
    public void Save(string refreshToken) => Token = refreshToken;
}

[Collection("environment")]
public sealed class TokenStoreTests
{
    [Fact]
    public void Token_file_is_encrypted_for_the_windows_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "youtube-token.bin");

        new DpapiTokenStore(path, "youtube").Save("1//refresh-token-value");

        Assert.DoesNotContain("refresh-token-value", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("1//refresh-token-value", new DpapiTokenStore(path, "youtube").Load());
        // Each platform's file has its own key: a TikTok store cannot read the YouTube token.
        Assert.Throws<PublishAuthException>(() => new DpapiTokenStore(path, "tiktok").Load());
    }

    [Fact]
    public void The_environment_variable_wins_over_the_file()
    {
        var variable = $"MINUTO_TEST_TOKEN_{Guid.NewGuid():N}";
        Assert.IsType<DpapiTokenStore>(TokenStores.For(variable, "%TEMP%/none.bin", "youtube"));
        Environment.SetEnvironmentVariable(variable, "env-refresh");
        try
        {
            var store = TokenStores.For(variable, "%TEMP%/none.bin", "youtube");
            Assert.IsType<EnvironmentTokenStore>(store);
            Assert.Equal("env-refresh", store.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void A_rotated_token_in_an_environment_variable_warns_without_printing_it()
    {
        var lines = new List<string>();
        var sink = Log.Sink;
        Log.Sink = line => { lock (lines) lines.Add(line); };
        try
        {
            var store = new EnvironmentTokenStore("MINUTO_TIKTOK_REFRESH_TOKEN", "rft.old-secret");
            // Other tests log at the same time through the shared sink: read a copy taken under the lock.
            List<string> Seen() { lock (lines) return [.. lines]; }
            store.Save("rft.old-secret");
            Assert.DoesNotContain(Seen(), l => l.Contains("MINUTO_TIKTOK_REFRESH_TOKEN"));

            store.Save("rft.new-secret");
            Assert.Equal("rft.new-secret", store.Load());
            Assert.Contains(Seen(), l => l.Contains("WARN") && l.Contains("MINUTO_TIKTOK_REFRESH_TOKEN"));
            Assert.DoesNotContain(Seen(), l => l.Contains("rft."));
        }
        finally
        {
            Log.Sink = sink;
        }
    }
}

public sealed class TikTokTests
{
    private const string UploadUrl = "https://open-upload.tiktokapis.com/video/?upload_id=7&upload_token=abc";

    [Fact]
    public void Small_videos_go_in_one_chunk()
    {
        Assert.Equal(new ChunkPlan(27_950_660, 27_950_660, 1), ChunkPlan.For(27_950_660));
        Assert.Equal(new ChunkPlan(4_000_000, 4_000_000, 1), ChunkPlan.For(4_000_000));
        Assert.Equal(1, ChunkPlan.For(64_000_000).TotalChunkCount);
    }

    [Fact]
    public void Large_videos_are_cut_into_chunks_with_the_remainder_on_the_last()
    {
        var plan = ChunkPlan.For(150_000_123);

        Assert.Equal(20_000_000, plan.ChunkSize);
        Assert.Equal(7, plan.TotalChunkCount);
        Assert.Equal((0, 19_999_999), plan.Range(0));
        Assert.Equal((120_000_000, 150_000_122), plan.Range(6));
        var sizes = Enumerable.Range(0, plan.TotalChunkCount).Select(i => plan.Range(i).Last - plan.Range(i).First + 1).ToList();
        Assert.Equal(150_000_123, sizes.Sum());
        // TikTok: 5 to 64 MB per chunk, the last one up to 128 MB (true in both decimal and binary megabytes).
        Assert.All(sizes.SkipLast(1), s => Assert.InRange(s, 5 * 1024 * 1024, 64_000_000));
        Assert.InRange(sizes[^1], 5 * 1024 * 1024, 128_000_000);
    }

    [Fact]
    public void Init_body_asks_for_a_file_upload()
    {
        var body = JsonDocument.Parse(TikTokUploader.InitBody(ChunkPlan.For(150_000_123))).RootElement.GetProperty("source_info");

        Assert.Equal("FILE_UPLOAD", body.GetProperty("source").GetString());
        Assert.Equal(150_000_123, body.GetProperty("video_size").GetInt64());
        Assert.Equal(20_000_000, body.GetProperty("chunk_size").GetInt64());
        Assert.Equal(7, body.GetProperty("total_chunk_count").GetInt32());
    }

    [Fact]
    public async Task One_chunk_upload_then_polls_until_the_draft_is_in_the_inbox()
    {
        using var temp = new TempFolder();
        var file = PublishData.RunFolder(temp, 1000);
        var tiktok = new FakeTikTok { Statuses = new(["PROCESSING_UPLOAD", "PROCESSING_UPLOAD", "SEND_TO_USER_INBOX"]) };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        var upload = await uploader.UploadAsync(Path.Combine(file, "video.mp4"), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal("v_inbox_file~v2.123", upload.PublishId);
        Assert.Equal("SEND_TO_USER_INBOX", upload.Status.Status);
        var init = tiktok.Requests[0];
        Assert.Equal(TikTokUploader.InitEndpoint, init.Url);
        Assert.Equal("Bearer act.1", init.Authorization);
        Assert.Equal(1, JsonDocument.Parse(init.Body!).RootElement.GetProperty("source_info").GetProperty("total_chunk_count").GetInt32());
        var put = tiktok.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal(UploadUrl, put.Url);
        Assert.Equal("bytes 0-999/1000", put.ContentRange);
        Assert.Equal("video/mp4", put.ContentType);
        Assert.Null(put.Authorization);
        var polls = tiktok.Requests.Where(r => r.Url == TikTokUploader.StatusEndpoint).ToList();
        Assert.Equal(3, polls.Count);
        Assert.All(polls, p => Assert.Equal("v_inbox_file~v2.123", JsonDocument.Parse(p.Body!).RootElement.GetProperty("publish_id").GetString()));
    }

    [Fact]
    public async Task A_150_MB_video_goes_up_in_seven_ordered_chunks_and_a_failed_chunk_is_sent_again()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "big.mp4");
        await using (var stream = File.Create(path)) stream.SetLength(150_000_123);
        var tiktok = new FakeTikTok { FailChunkOnce = 3 };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        await uploader.UploadAsync(path, TimeSpan.FromMinutes(1), CancellationToken.None);

        var ranges = tiktok.Requests.Where(r => r.Method == HttpMethod.Put).Select(r => r.ContentRange).ToList();
        Assert.Equal([
            "bytes 0-19999999/150000123", "bytes 20000000-39999999/150000123", "bytes 40000000-59999999/150000123",
            "bytes 60000000-79999999/150000123", "bytes 60000000-79999999/150000123", "bytes 80000000-99999999/150000123",
            "bytes 100000000-119999999/150000123", "bytes 120000000-150000122/150000123",
        ], ranges);
        Assert.Equal(30_000_123, tiktok.Requests.Last(r => r.Method == HttpMethod.Put).Length);
    }

    [Fact]
    public async Task A_failed_status_stops_with_the_reason()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var tiktok = new FakeTikTok { Statuses = new(["FAILED"]), FailReason = "duration_check_failed" };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TikTokUploadException>(() => uploader.UploadAsync(Path.Combine(folder, "video.mp4"), TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Contains("duration_check_failed", ex.Message);
    }

    [Fact]
    public async Task Still_processing_after_the_wait_is_left_to_tiktok()
    {
        var tiktok = new FakeTikTok { Statuses = new(["PROCESSING_UPLOAD"]) };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        var status = await uploader.WaitForInboxAsync("p1", TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("PROCESSING_UPLOAD", status.Status);
        Assert.Single(tiktok.Requests);
    }

    [Fact]
    public async Task Api_errors_are_reported_with_a_hint_and_not_retried()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var tiktok = new FakeTikTok { InitError = "spam_risk_too_many_pending_share" };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TikTokUploadException>(() => uploader.UploadAsync(Path.Combine(folder, "video.mp4"), TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Contains("5 drafts", ex.Message);
        Assert.Single(tiktok.Requests);
    }

    [Fact]
    public async Task An_upload_url_outside_tiktok_is_refused()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var tiktok = new FakeTikTok { UploadUrlOverride = "https://attacker.example/upload" };
        var uploader = new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("act.1"), TimeSpan.Zero, TimeSpan.Zero);

        await Assert.ThrowsAsync<TikTokUploadException>(() => uploader.UploadAsync(Path.Combine(folder, "video.mp4"), TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.DoesNotContain(tiktok.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void Consent_url_has_comma_scopes_and_a_hex_pkce_challenge()
    {
        var challenge = TikTokAuth.Challenge("verifier-verifier-verifier-verifier-verifier");
        var url = TikTokAuth.AuthorizationUrl("key-1", "http://localhost:8723/callback/", challenge, "st");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);

        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/?", url.AbsoluteUri);
        Assert.Equal("key-1", query["client_key"]);
        Assert.Equal("user.info.basic,video.upload", query["scope"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Matches("^[0-9a-f]{64}$", challenge);
        Assert.Equal(challenge, query["code_challenge"]);
    }

    [Fact]
    public async Task Pasted_redirect_sign_in_saves_the_token_and_refresh_rotation_is_kept()
    {
        var auth0 = new FakeTikTokAuth();
        var store = new MemoryTokenStore();
        var time = new FakeTimeProvider(PublishData.Now);
        var auth = new TikTokAuth(new HttpClient(auth0), new TikTokClient("key-1", "secret-1"), store, time);
        Uri? opened = null;

        var account = await auth.AuthorizeInteractiveAsync("http://localhost:8723/callback/", url => opened = url, () =>
        {
            var state = System.Web.HttpUtility.ParseQueryString(opened!.Query)["state"]!;
            return $"http://localhost:8723/callback/?code=code%2A1&scopes=user.info.basic%2Cvideo.upload&state={Uri.EscapeDataString(state)}";
        }, CancellationToken.None);

        Assert.Equal("Minuto Útil", account.DisplayName);
        Assert.Equal("rft.1", store.Token);
        var exchange = auth0.Forms[0];
        Assert.Equal("code*1", exchange["code"]);
        Assert.Equal("http://localhost:8723/callback/", exchange["redirect_uri"]);
        Assert.Equal("key-1", exchange["client_key"]);
        Assert.Equal(TikTokAuth.Challenge(exchange["code_verifier"]), System.Web.HttpUtility.ParseQueryString(opened!.Query)["code_challenge"]);

        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal("act.2", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("refresh_token", auth0.Forms[^1]["grant_type"]);
        Assert.Equal("rft.2", store.Token);
    }

    [Fact]
    public async Task A_pasted_address_with_another_state_is_refused()
    {
        var auth = new TikTokAuth(new HttpClient(new FakeTikTokAuth()), new TikTokClient("k", "s"), new MemoryTokenStore(), TimeProvider.System);

        await Assert.ThrowsAsync<PublishAuthException>(() => auth.AuthorizeInteractiveAsync("http://localhost:8723/callback/", _ => { },
            () => "http://localhost:8723/callback/?code=evil&state=forged", CancellationToken.None));
    }

    [Fact]
    public async Task Loopback_sign_in_works_on_the_configured_redirect()
    {
        var port = OAuthRedirect.FreeLoopbackPort();
        var redirect = $"http://localhost:{port}/callback/";
        var store = new MemoryTokenStore();
        var auth = new TikTokAuth(new HttpClient(new FakeTikTokAuth()), new TikTokClient("k", "s"), store, TimeProvider.System);

        await auth.AuthorizeInteractiveAsync(redirect, GoogleAuthTests.PlayBrowser("c1"), null, CancellationToken.None);

        Assert.Equal("rft.1", store.Token);
    }

    /// <summary>TikTok's token and user info endpoints; each refresh hands back a new refresh token.</summary>
    private sealed class FakeTikTokAuth : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri == TikTokAuth.UserInfoEndpoint)
                return FakeHandler.Json(HttpStatusCode.OK, """{"data":{"user":{"open_id":"o1","display_name":"Minuto Útil"}},"error":{"code":"ok"}}""");
            Assert.Equal(TikTokAuth.TokenEndpoint, request.RequestUri);
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            Forms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            var n = Forms.Count;
            return FakeHandler.Json(HttpStatusCode.OK,
                $$"""{"access_token":"act.{{n}}","expires_in":86400,"refresh_token":"rft.{{n}}","refresh_expires_in":31536000,"open_id":"o1","scope":"user.info.basic,video.upload","token_type":"Bearer"}""");
        }
    }

    /// <summary>TikTok's inbox init, the upload URL (206 per chunk, 201 at the end) and the status endpoint.</summary>
    internal sealed class FakeTikTok : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];
        public Queue<string> Statuses { get; init; } = new(["SEND_TO_USER_INBOX"]);
        public string? FailReason { get; init; }
        public string? InitError { get; init; }
        public string? UploadUrlOverride { get; init; }
        public int? FailChunkOnce { get; set; }
        private long _received;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var range = request.Content?.Headers.TryGetValues("Content-Range", out var values) == true ? values.First() : null;
            var url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(new SeenRequest(request.Method, url, request.Headers.Authorization?.ToString(), range,
                    request.Content?.Headers.ContentType?.MediaType, body.Length, request.Method == HttpMethod.Post ? Encoding.UTF8.GetString(body) : null));

            if (url == TikTokUploader.InitEndpoint)
            {
                _received = 0;
                if (InitError is { } error)
                    return FakeHandler.Json(HttpStatusCode.Forbidden, $$$"""{"error":{"code":"{{{error}}}","message":"too many","log_id":"l1"}}""");
                return FakeHandler.Json(HttpStatusCode.OK,
                    $$$"""{"data":{"publish_id":"v_inbox_file~v2.123","upload_url":"{{{UploadUrlOverride ?? UploadUrl}}}"},"error":{"code":"ok","message":"","log_id":"l1"}}""");
            }
            if (url == TikTokUploader.StatusEndpoint)
            {
                var status = Statuses.Count > 1 ? Statuses.Dequeue() : Statuses.Peek();
                var reason = FailReason is null ? "" : $$""","fail_reason":"{{FailReason}}" """;
                return FakeHandler.Json(HttpStatusCode.OK, $$$"""{"data":{"status":"{{{status}}}","uploaded_bytes":1000{{{reason}}}},"error":{"code":"ok"}}""");
            }

            Assert.Equal(UploadUrl, url);
            var parts = range!["bytes ".Length..].Split('/', '-');
            long first = long.Parse(parts[0]), last = long.Parse(parts[1]), total = long.Parse(parts[2]);
            Assert.Equal(last - first + 1, body.Length);
            Assert.Equal(_received, first);
            if (FailChunkOnce is { } failing && first == failing * 20_000_000L)
            {
                FailChunkOnce = null;
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            _received = last + 1;
            return new HttpResponseMessage(_received == total ? HttpStatusCode.Created : HttpStatusCode.PartialContent);
        }
    }
}

public sealed class PublisherTests
{
    [Fact]
    public async Task Dry_run_makes_no_http_calls_and_writes_nothing()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var before = Directory.GetFiles(folder).Order().ToList();
        var youtube = new FakeYouTube();
        var tiktok = new TikTokTests.FakeTikTok();
        var clientsMade = 0;
        var publisher = new Publisher(PublishData.YouTubeConfig, new TikTokOptions(),
            () => { clientsMade++; return new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero); },
            () => { clientsMade++; return new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("t"), TimeSpan.Zero, TimeSpan.Zero); },
            new FakeTimeProvider(PublishData.Now));

        var plan = await publisher.PublishAsync(folder, Platforms.Both, dryRun: true, force: false, CancellationToken.None);

        Assert.Equal(0, clientsMade);
        Assert.Empty(youtube.Requests);
        Assert.Empty(tiktok.Requests);
        Assert.Equal(before, Directory.GetFiles(folder).Order().ToList());
        Assert.Contains("title (46 chars): Como a Previsão do Tempo Mudou o Dia D #shorts", plan);
        Assert.Contains("privacy: public, category: 27, language: pt-BR", plan);
        Assert.Contains("1 chunk(s)", plan);
    }

    [Fact]
    public async Task Publishing_records_each_platform_and_refuses_a_second_time_unless_forced()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var youtube = new FakeYouTube();
        var tiktok = new TikTokTests.FakeTikTok();
        var publisher = new Publisher(PublishData.YouTubeConfig, new TikTokOptions(),
            () => new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero),
            () => new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("t"), TimeSpan.Zero, TimeSpan.Zero),
            new FakeTimeProvider(PublishData.Now));

        await publisher.PublishAsync(folder, Platforms.YouTube, dryRun: false, force: false, CancellationToken.None);

        // The channel is checked before anything is uploaded.
        Assert.Equal(HttpMethod.Get, youtube.Requests[0].Method);
        var record = Publisher.LoadRecord(folder);
        Assert.Equal("vid123", record.YouTube!.VideoId);
        Assert.Equal("https://youtube.com/shorts/vid123", record.YouTube.Url);
        Assert.Equal("UCNath7BzjeX3ehJCA7cNYAA", record.YouTube.ChannelId);
        Assert.Null(record.TikTok);
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, Publisher.RecordFile))).RootElement;
        Assert.True(json.TryGetProperty("youtube", out _));

        var uploadsBefore = youtube.Requests.Count;
        var ex = await Assert.ThrowsAsync<PublishRefusedException>(() => publisher.PublishAsync(folder, Platforms.Both, false, false, CancellationToken.None));
        Assert.Contains("--force", ex.Message);
        Assert.Equal(uploadsBefore, youtube.Requests.Count);
        Assert.Empty(tiktok.Requests);

        // TikTok alone is still allowed, and writes the caption for the owner.
        await publisher.PublishAsync(folder, Platforms.TikTok, false, false, CancellationToken.None);
        record = Publisher.LoadRecord(folder);
        Assert.Equal("v_inbox_file~v2.123", record.TikTok!.PublishId);
        Assert.Equal("SEND_TO_USER_INBOX", record.TikTok.Status);
        Assert.Equal("vid123", record.YouTube!.VideoId);
        Assert.Equal(PublishCopy.TikTokCaption(PublishData.Metadata(), new TikTokOptions()), File.ReadAllText(Path.Combine(folder, Publisher.CaptionFile)));

        await Assert.ThrowsAsync<PublishRefusedException>(() => publisher.PublishAsync(folder, Platforms.TikTok, false, false, CancellationToken.None));
        await publisher.PublishAsync(folder, Platforms.TikTok, false, force: true, CancellationToken.None);
        Assert.Equal(2, tiktok.Requests.Count(r => r.Url == TikTokUploader.InitEndpoint));
    }

    [Fact]
    public async Task A_tiktok_draft_sends_its_caption_to_telegram_as_its_own_message()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var telegram = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, """{"ok":true}"""));
        var notifier = new TelegramNotifier(new HttpClient(telegram), "123:secret", "42");
        var publisher = new Publisher(PublishData.YouTubeConfig, new TikTokOptions(),
            () => throw new InvalidOperationException("YouTube must not be reached"),
            () => new TikTokUploader(new HttpClient(new TikTokTests.FakeTikTok()), _ => Task.FromResult("t"), TimeSpan.Zero, TimeSpan.Zero),
            new FakeTimeProvider(PublishData.Now), notifier.SendTextAsync);

        var summary = await publisher.PublishAsync(folder, Platforms.TikTok, false, false, CancellationToken.None);

        Assert.Equal(2, telegram.Requests.Count);
        Assert.All(telegram.Requests, r => Assert.Equal("https://api.telegram.org/bot123:secret/sendMessage", r.Request.RequestUri!.AbsoluteUri));
        var last = JsonDocument.Parse(telegram.Requests[1].Body).RootElement;
        Assert.Equal("42", last.GetProperty("chat_id").GetString());
        Assert.Equal(File.ReadAllText(Path.Combine(folder, Publisher.CaptionFile)), last.GetProperty("text").GetString());
        Assert.Contains("Telegram: caption sent.", summary);
    }

    [Fact]
    public async Task A_telegram_failure_keeps_the_draft_and_never_shows_the_bot_token()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var telegram = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}"""));
        var notifier = new TelegramNotifier(new HttpClient(telegram), "123:secret", "42");
        var publisher = new Publisher(PublishData.YouTubeConfig, new TikTokOptions(),
            () => throw new InvalidOperationException("YouTube must not be reached"),
            () => new TikTokUploader(new HttpClient(new TikTokTests.FakeTikTok()), _ => Task.FromResult("t"), TimeSpan.Zero, TimeSpan.Zero),
            new FakeTimeProvider(PublishData.Now), notifier.SendTextAsync);

        var summary = await publisher.PublishAsync(folder, Platforms.TikTok, false, false, CancellationToken.None);

        Assert.Equal("v_inbox_file~v2.123", Publisher.LoadRecord(folder).TikTok!.PublishId);
        Assert.Contains("chat not found", summary);
        Assert.DoesNotContain("secret", summary);
    }

    [Fact]
    public async Task Nothing_is_uploaded_to_another_channel()
    {
        using var temp = new TempFolder();
        var folder = PublishData.RunFolder(temp);
        var youtube = new FakeYouTube { ChannelId = "UCflopometro", ChannelTitle = "Flopômetro" };
        var publisher = new Publisher(PublishData.YouTubeConfig, new TikTokOptions(),
            () => new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero),
            () => throw new InvalidOperationException("TikTok must not be reached"),
            new FakeTimeProvider(PublishData.Now));

        await Assert.ThrowsAsync<YouTubeUploadException>(() => publisher.PublishAsync(folder, Platforms.YouTube, false, false, CancellationToken.None));

        Assert.Single(youtube.Requests);
        Assert.False(File.Exists(Path.Combine(folder, Publisher.RecordFile)));
    }
}
