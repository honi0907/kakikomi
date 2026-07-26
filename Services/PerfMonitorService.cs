using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;

namespace Kakikomi.Services;

/// <summary>
/// 自プロセスの CPU / メモリと、遠隔プレビュー送信 fps を計測する。
/// 表示用サンプリングは 1 秒、ファイルログは平常 30 秒・危険時 5 秒。
/// </summary>
public sealed class PerfMonitorService : IDisposable
{
    public static PerfMonitorService Instance { get; } = new();

    private const double DangerCpuPercent = 80.0;
    private const double DangerMemoryMb = 2500.0;
    private const int PeriodicLogIntervalMs = 30_000;
    private const int DangerLogIntervalMs = 5_000;
    private const int LogRetainDays = 7;

    private readonly object _gate = new();
    private Process? _process;
    private TimeSpan _lastCpu;
    private long _lastCpuStampMs;
    private DispatcherQueueTimer? _timer;
    private int _previewFrames;
    private long _previewBytes;
    private int _previewFps;
    private double _previewKBps;
    private double _cpuPercent;
    private double _memoryMb;
    private long _lastPeriodicLogMs;
    private long _lastDangerLogMs;
    private bool _running;
    private bool _disposed;
    private bool _cleanupDone;
    private bool _logSession;
    private string? _lastEventKey;
    private long _lastEventLogMs;

    public event Action? Updated;

    public double CpuPercent
    {
        get { lock (_gate) return _cpuPercent; }
    }

    public double MemoryMb
    {
        get { lock (_gate) return _memoryMb; }
    }

    public int PreviewFps
    {
        get { lock (_gate) return _previewFps; }
    }

    public double PreviewKBps
    {
        get { lock (_gate) return _previewKBps; }
    }

    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            "logs");

    public string OverlayText
    {
        get
        {
            lock (_gate)
            {
                var remote = AppSettings.RemoteControlEnabled
                    ? $"  |  Preview {_previewFps} fps  {_previewKBps:0} KB/s"
                    : "";
                return $"CPU {_cpuPercent:0.0}%  |  Mem {_memoryMb:0} MB{remote}";
            }
        }
    }

    public void ApplyFromSettings()
    {
        var needRun = AppSettings.PerfMonitorEnabled || AppSettings.PerfLogEnabled;
        if (!needRun)
        {
            Stop();
            return;
        }

        if (!_running)
        {
            Start();
            return;
        }

        // 計測中にログだけ ON/OFF
        if (AppSettings.PerfLogEnabled && !_logSession)
        {
            _logSession = true;
            _lastPeriodicLogMs = 0;
            AppendLog("INFO", "perf log started", force: true);
        }
        else if (!AppSettings.PerfLogEnabled && _logSession)
        {
            AppendLog("INFO", "perf log stopped", force: true);
            _logSession = false;
        }
    }

    public void Start()
    {
        if (_disposed)
            return;

        var dq = App.DispatcherQueue;
        if (dq is null)
            return;

        if (_running)
            return;

        _process = Process.GetCurrentProcess();
        _process.Refresh();
        _lastCpu = _process.TotalProcessorTime;
        _lastCpuStampMs = Environment.TickCount64;
        _lastPeriodicLogMs = 0;
        _lastDangerLogMs = 0;
        Interlocked.Exchange(ref _previewFrames, 0);
        Interlocked.Exchange(ref _previewBytes, 0);

        if (!_cleanupDone)
        {
            _cleanupDone = true;
            try { CleanupOldLogs(); } catch { /* ignore */ }
        }

        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
        _running = true;
        _logSession = AppSettings.PerfLogEnabled;
        SampleOnce();

        if (_logSession)
            AppendLog("INFO", "perf log started", force: true);
    }

    public void Stop()
    {
        if (!_running)
            return;

        if (_logSession)
            AppendLog("INFO", "perf log stopped", force: true);
        _logSession = false;

        _running = false;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
    }

    /// <summary>遠隔 JPEG 1枚送信時に呼ぶ。</summary>
    public void RecordPreviewFrame(int byteLength)
    {
        if (!_running || byteLength <= 0)
            return;
        Interlocked.Increment(ref _previewFrames);
        Interlocked.Add(ref _previewBytes, byteLength);
    }

    /// <summary>
    /// 映像経路など重要イベントをログへ残す。
    /// Perf ログ設定が OFF でも WARN/ERROR は必ず書く。
    /// </summary>
    public void LogEvent(string level, string message)
    {
        var upper = level.Trim().ToUpperInvariant();
        var force = upper is "WARN" or "ERROR" or "FATAL";
        if (!force && !AppSettings.PerfLogEnabled)
            return;

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (force && _lastEventKey == message && now - _lastEventLogMs < 5_000)
                return;
            _lastEventKey = message;
            _lastEventLogMs = now;
        }

        AppendLog(upper, message, force: true);
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerfMonitor] open logs: {ex.Message}");
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => SampleOnce();

    private void SampleOnce()
    {
        try
        {
            var proc = _process ?? Process.GetCurrentProcess();
            proc.Refresh();

            var nowCpu = proc.TotalProcessorTime;
            var nowMs = Environment.TickCount64;
            var wallMs = Math.Max(1, nowMs - _lastCpuStampMs);
            var cpuMs = (nowCpu - _lastCpu).TotalMilliseconds;
            var cores = Math.Max(1, Environment.ProcessorCount);
            var cpu = Math.Clamp(cpuMs / wallMs / cores * 100.0, 0, 100.0 * cores);

            _lastCpu = nowCpu;
            _lastCpuStampMs = nowMs;

            var frames = Interlocked.Exchange(ref _previewFrames, 0);
            var bytes = Interlocked.Exchange(ref _previewBytes, 0);
            var memMb = proc.WorkingSet64 / (1024.0 * 1024.0);

            lock (_gate)
            {
                _cpuPercent = cpu;
                _memoryMb = memMb;
                _previewFps = frames;
                _previewKBps = bytes / 1024.0;
            }

            if (AppSettings.PerfMonitorEnabled)
                Updated?.Invoke();

            if (AppSettings.PerfLogEnabled && _logSession)
                MaybeWriteLog(cpu, memMb, frames, bytes / 1024.0, nowMs);

            VideoPipelineRecovery.TickHealthCheck();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerfMonitor] {ex.Message}");
        }
    }

    private void MaybeWriteLog(double cpu, double memMb, int fps, double kbps, long nowMs)
    {
        var dangerReasons = new List<string>(2);
        if (cpu >= DangerCpuPercent)
            dangerReasons.Add($"cpu>={DangerCpuPercent:0}");
        if (memMb >= DangerMemoryMb)
            dangerReasons.Add($"mem>={DangerMemoryMb:0}");

        if (dangerReasons.Count > 0)
        {
            if (nowMs - _lastDangerLogMs >= DangerLogIntervalMs)
            {
                _lastDangerLogMs = nowMs;
                _lastPeriodicLogMs = nowMs;
                AppendLog(
                    "WARN",
                    FormatMetrics(cpu, memMb, fps, kbps) + " reason=" + string.Join(",", dangerReasons),
                    force: true);
            }

            return;
        }

        if (_lastPeriodicLogMs == 0 || nowMs - _lastPeriodicLogMs >= PeriodicLogIntervalMs)
        {
            _lastPeriodicLogMs = nowMs;
            AppendLog("INFO", FormatMetrics(cpu, memMb, fps, kbps), force: true);
        }
    }

    private static string FormatMetrics(double cpu, double memMb, int fps, double kbps) =>
        $"CPU={cpu:0.0}% Mem={memMb:0}MB Preview={fps}fps {kbps:0}KB/s";

    private void AppendLog(string level, string message, bool force)
    {
        if (!force && !AppSettings.PerfLogEnabled)
            return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"perf-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerfMonitor] log write: {ex.Message}");
        }
    }

    private static void CleanupOldLogs()
    {
        if (!Directory.Exists(LogDirectory))
            return;

        var cutoff = DateTime.Now.Date.AddDays(-LogRetainDays);
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "perf-*.log"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                // perf-yyyyMMdd
                if (name.Length >= 13 &&
                    DateTime.TryParseExact(
                        name.AsSpan(5),
                        "yyyyMMdd",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out var day) &&
                    day < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // ignore per-file
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
