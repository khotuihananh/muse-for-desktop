using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MuseDesktop;

/// <summary>
/// Screen recording via the bundled ffmpeg.exe (gdigrab → libx264 MP4).
/// Start captures one whole monitor in physical pixels; Stop sends 'q'
/// to ffmpeg's stdin so it finalises the MP4 cleanly, then the caller
/// pastes the file into the chat. No audio track (screen-only capture).
/// Windows' own Snipping Tool cannot be used for this: it exposes no
/// start/stop control and never hands the recorded file back, so the
/// press-once / press-again / auto-paste flow is impossible on top of it.
/// </summary>
public sealed class Recorder
{
    private Process? _proc;
    private readonly StringBuilder _stderrTail = new();

    public bool IsRecording => _proc != null && !_proc.HasExited;
    public int ScreenIndex { get; private set; } = -1;
    public string? OutputPath { get; private set; }
    public DateTime StartedAt { get; private set; }

    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    public static bool Available => File.Exists(FfmpegPath);

    public static string RecordingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop", "recordings");

    /// <summary>Start recording. Returns null on success, else an error message.</summary>
    public string? Start(int screenIndex, Rectangle rect)
    {
        if (IsRecording) return "Đang có một bản quay khác chạy.";
        if (!Available) return "Không thấy ffmpeg.exe cạnh MuseDesktop.exe.";
        try
        {
            Directory.CreateDirectory(RecordingsDir);
            var path = Path.Combine(RecordingsDir,
                $"man-{screenIndex + 1}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                Arguments =
                    $"-y -f gdigrab -framerate 30 -offset_x {rect.X} -offset_y {rect.Y} " +
                    $"-video_size {rect.Width}x{rect.Height} -i desktop -an " +
                    $"-vf \"scale=iw-mod(iw\\,2):ih-mod(ih\\,2)\" " +
                    $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            _stderrTail.Clear();
            _proc = Process.Start(psi);
            if (_proc == null) return "Không khởi động được ffmpeg.";
            var proc = _proc;
            // ffmpeg logs progress to stderr continuously; drain it (a full
            // pipe would stall the encode) and keep a tail for diagnostics.
            _ = Task.Run(() =>
            {
                try
                {
                    var buf = new char[4096];
                    int n;
                    while ((n = proc.StandardError.Read(buf, 0, buf.Length)) > 0)
                    {
                        lock (_stderrTail)
                        {
                            _stderrTail.Append(buf, 0, n);
                            if (_stderrTail.Length > 4000)
                                _stderrTail.Remove(0, _stderrTail.Length - 4000);
                        }
                    }
                }
                catch { /* Process ended. */ }
            });
            ScreenIndex = screenIndex;
            OutputPath = path;
            StartedAt = DateTime.Now;
            return null;
        }
        catch (Exception ex)
        {
            _proc = null;
            return ex.Message;
        }
    }

    public string StderrTail
    {
        get { lock (_stderrTail) return _stderrTail.ToString(); }
    }

    /// <summary>Stop gracefully ('q' → ffmpeg finalises the MP4). Returns the file path or null.</summary>
    public async Task<string?> StopAsync()
    {
        var proc = _proc;
        var path = OutputPath;
        _proc = null;
        ScreenIndex = -1;
        OutputPath = null;
        if (proc == null) return null;
        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    await proc.StandardInput.WriteLineAsync("q");
                    await proc.StandardInput.FlushAsync();
                }
                catch { /* Pipe already closed. */ }
                var done = await Task.Run(() => proc.WaitForExit(6000));
                if (!done && !proc.HasExited)
                {
                    try { proc.Kill(); } catch { /* Already gone. */ }
                }
            }
        }
        catch { /* Best effort. */ }
        if (path != null && File.Exists(path) && new FileInfo(path).Length > 0)
            return path;
        return null;
    }
}
