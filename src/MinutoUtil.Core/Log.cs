namespace MinutoUtil.Core;

public static class Log
{
    private static readonly Lock Gate = new();

    public static Action<string> Sink { get; set; } = line => Console.Error.WriteLine(line);

    public static void Info(string message) => Write($"{DateTime.Now:HH:mm:ss} {message}");

    public static void Warn(string message) => Write($"{DateTime.Now:HH:mm:ss} WARN {message}");

    private static void Write(string line)
    {
        lock (Gate) Sink(line);
    }
}
