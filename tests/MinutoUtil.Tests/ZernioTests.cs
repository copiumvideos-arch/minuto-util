using System.Net;
using System.Text.Json;
using MinutoUtil.Core.Publishing;

namespace MinutoUtil.Tests;

/// <summary>TikTok posts through Zernio: the account found by username, the upload, the public post and its refusals.</summary>
public sealed class ZernioTests
{
    private const string UploadUrl = "https://bucket.r2.cloudflarestorage.com/temp/v.mp4?X-Amz-Signature=x";

    /// <summary>Zernio with two connected TikTok accounts; <paramref name="post"/> answers the create-post call.</summary>
    internal static FakeHandler FakeZernio(HttpStatusCode postStatus = HttpStatusCode.Created, string? post = null) => new((request, _) =>
    {
        var url = request.RequestUri!.ToString();
        if (url == ZernioPoster.Api + "accounts?platform=tiktok")
            return FakeHandler.Json(HttpStatusCode.OK, """
                {"accounts":[{"_id":"acc-minuto","platform":"tiktok","username":"minutoutil"},
                             {"_id":"acc-spoiler","platform":"tiktok","username":"profissao.spoiler"}],"hasAnalyticsAccess":false}
                """);
        if (url == ZernioPoster.Api + "media/presign")
            return FakeHandler.Json(HttpStatusCode.OK,
                $$"""{"uploadUrl":"{{UploadUrl}}","publicUrl":"https://media.zernio.com/temp/v.mp4","key":"temp/v.mp4","expiresIn":3600}""");
        if (url == UploadUrl) return new HttpResponseMessage(HttpStatusCode.OK);
        if (url == ZernioPoster.Api + "posts")
            return FakeHandler.Json(postStatus, post ?? """
                {"message":"Post published","post":{"_id":"post-1","status":"published","platforms":[
                  {"platform":"tiktok","accountId":"acc-spoiler","status":"published","platformPostUrl":"https://www.tiktok.com/@profissao.spoiler/video/7"}]}}
                """);
        throw new InvalidOperationException($"unexpected call {request.Method} {url}");
    });

    private static string Video(TempFolder temp)
    {
        var path = Path.Combine(temp.Path, "video.mp4");
        File.WriteAllBytes(path, new byte[1000]);
        return path;
    }

    [Fact]
    public async Task A_video_is_posted_public_to_the_named_account_and_the_key_never_reaches_storage()
    {
        using var temp = new TempFolder();
        var zernio = FakeZernio();

        var post = await new ZernioPoster(new HttpClient(zernio), "zk").PostAsync(Video(temp), "Legenda #games", "@profissao.spoiler", "run-1", CancellationToken.None);

        Assert.Equal(new ZernioPost("post-1", "published", "https://www.tiktok.com/@profissao.spoiler/video/7"), post);
        var put = Assert.Single(zernio.Requests, r => r.Request.Method == HttpMethod.Put);
        Assert.Null(put.Request.Headers.Authorization);
        Assert.Equal("video/mp4", put.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(1000, put.Body.Length);

        var create = Assert.Single(zernio.Requests, r => r.Request.RequestUri!.ToString() == ZernioPoster.Api + "posts");
        Assert.Equal("Bearer zk", create.Request.Headers.Authorization!.ToString());
        Assert.Equal("run-1", create.Request.Headers.GetValues("Idempotency-Key").Single());
        var body = JsonDocument.Parse(create.Body).RootElement;
        Assert.Equal("Legenda #games", body.GetProperty("content").GetString());
        Assert.Equal("https://media.zernio.com/temp/v.mp4", body.GetProperty("mediaItems")[0].GetProperty("url").GetString());
        Assert.Equal("acc-spoiler", body.GetProperty("platforms")[0].GetProperty("accountId").GetString());
        Assert.True(body.GetProperty("publishNow").GetBoolean());
        var settings = body.GetProperty("tiktokSettings");
        Assert.Equal("PUBLIC_TO_EVERYONE", settings.GetProperty("privacy_level").GetString());
        Assert.True(settings.GetProperty("express_consent_given").GetBoolean());
        Assert.True(settings.GetProperty("content_preview_confirmed").GetBoolean());
    }

    [Fact]
    public async Task An_account_not_connected_to_the_key_stops_before_any_upload()
    {
        using var temp = new TempFolder();
        var zernio = FakeZernio();

        var error = await Assert.ThrowsAsync<ZernioException>(() =>
            new ZernioPoster(new HttpClient(zernio), "zk").PostAsync(Video(temp), "x", "outra.conta", "run-1", CancellationToken.None));

        Assert.Contains("@outra.conta", error.Message);
        Assert.Contains("@minutoutil, @profissao.spoiler", error.Message);
        Assert.Single(zernio.Requests);
    }

    [Fact]
    public async Task A_refused_post_is_an_error_but_one_zernio_retries_itself_is_not()
    {
        using var temp = new TempFolder();
        var failed = FakeZernio((HttpStatusCode)207, """
            {"post":{"_id":"post-2","status":"failed","platforms":[{"platform":"tiktok","status":"failed","errorMessage":"Video too short"}]},"error":"No platform published"}
            """);
        var error = await Assert.ThrowsAsync<ZernioException>(() =>
            new ZernioPoster(new HttpClient(failed), "zk").PostAsync(Video(temp), "x", "profissao.spoiler", "run-2", CancellationToken.None));
        Assert.Contains("Video too short", error.Message);

        var retried = FakeZernio((HttpStatusCode)207, """
            {"post":{"_id":"post-3","status":"scheduled","platforms":[{"platform":"tiktok","status":"pending"}]}}
            """);
        var post = await new ZernioPoster(new HttpClient(retried), "zk").PostAsync(Video(temp), "x", "profissao.spoiler", "run-3", CancellationToken.None);
        Assert.Equal(new ZernioPost("post-3", "pending", null), post);
    }

    [Fact]
    public async Task A_wrong_key_says_which_setting_to_check()
    {
        using var temp = new TempFolder();
        var zernio = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Unauthorized, """{"error":"Invalid API key","code":"invalid_credentials"}"""));

        var error = await Assert.ThrowsAsync<ZernioException>(() =>
            new ZernioPoster(new HttpClient(zernio), "bad").PostAsync(Video(temp), "x", "profissao.spoiler", "run-1", CancellationToken.None));

        Assert.Contains("401 invalid_credentials", error.Message);
        Assert.Contains("ZERNIO_API_KEY", error.Message);
    }
}
