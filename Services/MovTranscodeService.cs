using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace Kakikomi.Services;

/// <summary>
/// .mov を再生用 H.264 mp4 へ変換する（元ファイルは削除しない）。
/// 変換結果は %LocalAppData%\Kakikomi\converted に置く。
/// </summary>
public static class MovTranscodeService
{
    private static readonly object Gate = new();

    public static string CacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            "converted");

    public static string EnsureCacheDirectory()
    {
        Directory.CreateDirectory(CacheDirectory);
        return CacheDirectory;
    }

    public static void OpenCacheInExplorer()
    {
        var path = EnsureCacheDirectory();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = path,
            UseShellExecute = true
        });
    }

    public static bool IsMovPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".mov", StringComparison.OrdinalIgnoreCase);

    /// <summary>再生に使う実ファイル。変換済みならキャッシュ／隣の mp4、なければ元パス。</summary>
    public static string ResolvePlaybackPath(string sourcePath)
    {
        if (!IsMovPath(sourcePath))
            return sourcePath;

        var cached = TryGetCachedMp4(sourcePath);
        if (!string.IsNullOrEmpty(cached))
            return cached;

        var sibling = Path.ChangeExtension(sourcePath, ".mp4");
        if (File.Exists(sibling))
            return sibling;

        return sourcePath;
    }

    public static bool NeedsConvert(string sourcePath)
    {
        if (!IsMovPath(sourcePath) || !File.Exists(sourcePath))
            return false;

        if (!string.IsNullOrEmpty(TryGetCachedMp4(sourcePath)))
            return false;

        var sibling = Path.ChangeExtension(sourcePath, ".mp4");
        return !File.Exists(sibling);
    }

    public static IReadOnlyList<string> GetPendingMovPaths(IEnumerable<string> paths) =>
        paths
            .Where(NeedsConvert)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string? TryGetCachedMp4(string sourcePath)
    {
        try
        {
            var mp4 = GetCacheMp4Path(sourcePath);
            if (!File.Exists(mp4))
                return null;

            var metaPath = mp4 + ".meta";
            if (!File.Exists(metaPath))
                return null;

            var meta = File.ReadAllText(metaPath).Trim();
            var expected = BuildSourceStamp(sourcePath);
            if (!string.Equals(meta, expected, StringComparison.Ordinal))
                return null;

            return mp4;
        }
        catch
        {
            return null;
        }
    }

    public static string? FindFfmpegPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        try
        {
            var fromPath = FindOnPath("ffmpeg.exe");
            if (!string.IsNullOrEmpty(fromPath))
                return fromPath;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static async Task ConvertAsync(
        string sourcePath,
        Action<string>? status = null,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!NeedsConvert(sourcePath))
            return;

        var ffmpeg = FindFfmpegPath()
            ?? throw new InvalidOperationException(
                "ffmpeg が見つかりません。アプリ同梱の ffmpeg を確認してください。");

        Directory.CreateDirectory(CacheDirectory);

        var output = GetCacheMp4Path(sourcePath);
        var temp = output + ".partial.mp4";
        var metaPath = output + ".meta";

        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch
        {
            // ignore
        }

        status?.Invoke($"変換中: {Path.GetFileName(sourcePath)}");
        progress?.Invoke(0);

        var duration = await TryGetDurationAsync(sourcePath).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-nostats",
                "-progress", "pipe:1",
                "-i", sourcePath,
                "-map", "0:v:0?",
                "-map", "0:a:0?",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "20",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-b:a", "192k",
                "-movflags", "+faststart",
                temp
            }
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();

        if (!process.Start())
            throw new InvalidOperationException("ffmpeg を起動できませんでした。");

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                stderr.AppendLine(line);
        }, cancellationToken);

        var progressTask = ReadFfmpegProgressAsync(
            process.StandardOutput,
            duration,
            progress,
            cancellationToken);

        using var reg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await progressTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore progress read after exit
        }

        try
        {
            await stderrTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            TryDelete(temp);
            var detail = stderr.ToString().Trim();
            if (string.IsNullOrEmpty(detail))
                detail = $"exit={process.ExitCode}";
            throw new InvalidOperationException($"変換失敗: {Path.GetFileName(sourcePath)} ({detail})");
        }

        lock (Gate)
        {
            TryDelete(output);
            File.Move(temp, output, overwrite: true);
            File.WriteAllText(metaPath, BuildSourceStamp(sourcePath));
        }

        progress?.Invoke(1);
        status?.Invoke($"変換完了: {Path.GetFileName(sourcePath)}");
    }

    private static async Task ReadFfmpegProgressAsync(
        StreamReader stdout,
        TimeSpan duration,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            try
            {
                await stdout.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            return;
        }

        var durationMs = duration.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (line is null)
                break;

            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var us)
                && us >= 0)
            {
                ReportProgressFromSeconds(us / 1_000_000.0, durationMs, progress);
            }
            else if (line.StartsWith("out_time=", StringComparison.Ordinal)
                && TryParseFfmpegTime(line.AsSpan("out_time=".Length), out var t))
            {
                ReportProgressFromSeconds(t.TotalSeconds, durationMs, progress);
            }
            // 注意: ffmpeg の out_time_ms は実際にはマイクロ秒
            else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_ms=".Length), out var raw)
                && raw >= 0)
            {
                ReportProgressFromSeconds(raw / 1_000_000.0, durationMs, progress);
            }
            else if (string.Equals(line, "progress=end", StringComparison.Ordinal))
            {
                progress(1);
            }
        }
    }

    private static void ReportProgressFromSeconds(
        double seconds,
        double durationMs,
        Action<double> progress)
    {
        if (durationMs > 0)
            progress(Math.Clamp(seconds * 1000.0 / durationMs, 0, 0.995));
        else
            progress(Math.Clamp(1.0 - (1.0 / (1.0 + Math.Max(seconds, 0) / 8.0)), 0, 0.95));
    }

    private static bool TryParseFfmpegTime(ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = default;
        // HH:MM:SS.microseconds
        var s = text.Trim().ToString();
        if (string.IsNullOrEmpty(s) || s == "N/A")
            return false;

        var parts = s.Split(':');
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[0], out var h))
            return false;
        if (!int.TryParse(parts[1], out var m))
            return false;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
            return false;

        time = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(sec);
        return true;
    }

    private static async Task<TimeSpan> TryGetDurationAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            if (props.Duration > TimeSpan.Zero)
                return props.Duration;
        }
        catch
        {
            // ignore
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            if (clip.OriginalDuration > TimeSpan.Zero)
                return clip.OriginalDuration;
        }
        catch
        {
            // ignore
        }

        return TryGetDurationViaFfmpeg(path);
    }

    private static TimeSpan TryGetDurationViaFfmpeg(string path)
    {
        var ffmpeg = FindFfmpegPath();
        if (ffmpeg is null)
            return TimeSpan.Zero;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    ArgumentList = { "-hide_banner", "-i", path }
                }
            };

            if (!process.Start())
                return TimeSpan.Zero;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            // Duration: 00:01:23.45
            const string marker = "Duration: ";
            var idx = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return TimeSpan.Zero;

            var start = idx + marker.Length;
            var end = stderr.IndexOf(',', start);
            if (end < 0)
                end = Math.Min(start + 16, stderr.Length);
            var token = stderr.AsSpan(start, end - start).Trim();
            return TryParseFfmpegTime(token, out var t) ? t : TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public static async Task ConvertAllAsync(
        IReadOnlyList<string> sourcePaths,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var pending = GetPendingMovPaths(sourcePaths);
        var failures = new List<string>();
        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pending[i];
            status?.Invoke($"変換中 {i + 1}/{pending.Count}: {Path.GetFileName(path)}");
            try
            {
                await ConvertAsync(path, status, progress: null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                status?.Invoke($"変換失敗 {i + 1}/{pending.Count}: {Path.GetFileName(path)}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failures.Count} 本の変換に失敗しました。{failures[0]}");
        }
    }

    private static string GetCacheMp4Path(string sourcePath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(sourcePath).ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, key + ".mp4");
    }

    private static string BuildSourceStamp(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Path.GetFullPath(sourcePath)}";
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
