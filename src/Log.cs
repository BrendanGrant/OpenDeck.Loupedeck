using System.IO;

namespace OpenDeck.Loupedeck;

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly System.Diagnostics.Stopwatch Elapsed = System.Diagnostics.Stopwatch.StartNew();
    private static readonly Lazy<string?> FilePathValue = new(CreateLogFile);

    public static void Info(string message) => Write(Console.Out, message);

    public static void Error(string message) => Write(Console.Error, message);

    public static string? FilePath => FilePathValue.Value;

    private static void Write(TextWriter writer, string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"[{now:HH:mm:ss.fff} +{Elapsed.Elapsed.TotalSeconds,8:0.000}s] {message}";
        lock (Gate)
        {
            writer.WriteLine(line);
            if (FilePath is not null)
                File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }

    private static string? CreateLogFile()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "opendeck-loupedeck",
                "logs");
            Directory.CreateDirectory(root);
            return Path.Combine(root, $"plugin-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        }
        catch
        {
            return null;
        }
    }
}
