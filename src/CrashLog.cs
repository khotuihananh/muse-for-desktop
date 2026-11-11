using System;
using System.IO;

namespace MuseDesktop;

internal static class CrashLog
{
    public static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop");

    public static string FilePath => Path.Combine(FolderPath, "crash.log");

    public static void Write(string source, Exception? ex)
    {
        WriteNote(source, ex?.ToString() ?? "");
    }

    public static void WriteNote(string source, string message)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.AppendAllText(
                FilePath,
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + source + ": " + message + Environment.NewLine);
        }
        catch
        {
            // Logging must never take the app down with it.
        }
    }
}
