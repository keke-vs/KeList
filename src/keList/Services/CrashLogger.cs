using System.IO;

namespace KeList.Services;

internal static class CrashLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "keList");

    private static readonly string LogPath = Path.Combine(LogDirectory, "errors.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never prevent the application from starting.
        }
    }

    public static void Write(Exception exception)
        => Write(exception.ToString());
}
