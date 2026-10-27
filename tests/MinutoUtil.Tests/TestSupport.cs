using System.Net;
using System.Text;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Tests;

/// <summary>An HTTP handler that answers from a function and records every request (the network replaced).</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minuto-util-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    /// <summary>Set MINUTO_KEEP_TEST_OUTPUT=1 to keep the folder and look at what a test rendered.</summary>
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("MINUTO_KEEP_TEST_OUTPUT") == "1")
        {
            Console.WriteLine($"kept {Path}");
            return;
        }
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class TestPaths
{
    public static string RepositoryRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "MinutoUtil.slnx"))) return dir.FullName;
            throw new DirectoryNotFoundException("MinutoUtil.slnx not found above the test folder.");
        }
    }

    public static string Fixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

/// <summary>Generated test media: FFmpeg's test sources, so the end-to-end tests need no downloads.</summary>
public static class Generated
{
    public static async Task ToneAsync(string path, double seconds, int frequency = 440, int sampleRate = 24000)
    {
        // A tone with a short silence at both ends, like a real take.
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate={sampleRate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
             "-af", "adelay=150,apad=pad_dur=0.2,volume=0.5", "-ac", "1", "-c:a", "pcm_s16le", path],
            System.IO.Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    /// <summary>A landscape picture with shapes at several sizes, so motion and parallax are visible.</summary>
    public static async Task ImageAsync(string path, int width, int height, string source = "testsrc2")
    {
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"{source}=size={width}x{height}:rate=1", "-frames:v", "1", "-q:v", "2", path],
            System.IO.Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    /// <summary>A depth map: a gradient from far (top) to near (bottom), 16-bit gray.</summary>
    public static async Task DepthAsync(string path, int width, int height)
    {
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=black:size={width}x{height}", "-frames:v", "1",
             "-vf", "geq=lum='255*Y/H',format=gray16be", path],
            System.IO.Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    public static async Task VideoAsync(string path, double seconds, int width = 1280, int height = 720)
    {
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", path],
            System.IO.Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(60), CancellationToken.None);
    }
}
