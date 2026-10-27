using System.Diagnostics;
using System.Text;

namespace MinutoUtil.Core.Video;

public sealed class ToolException(string message) : Exception(message);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs external tools (ffmpeg, uv). Arguments go through ArgumentList, never a shell, so text from
/// feeds or scripts cannot inject commands.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolException($"Cannot start '{fileName}'. Is it installed and on PATH? ({ex.Message})");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new ToolException($"'{Path.GetFileName(fileName)}' timed out after {timeout.TotalMinutes:0.#} min.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Like <see cref="RunAsync"/>, but a non-zero exit code throws with the end of stderr.</summary>
    public static async Task<ProcessResult> RunCheckedAsync(
        string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunAsync(fileName, arguments, workingDirectory, timeout, ct);
        if (result.ExitCode != 0)
        {
            var tail = result.StandardError.Length > 1500 ? result.StandardError[^1500..] : result.StandardError;
            throw new ToolException($"'{Path.GetFileName(fileName)}' exited with {result.ExitCode}: {tail.Trim()}");
        }
        return result;
    }
}
