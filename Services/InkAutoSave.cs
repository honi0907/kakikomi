using Microsoft.UI.Dispatching;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// ペン描画完了から 1 秒後に、映像＋書き込みの合成 PNG を自動保存（連続描画はデバウンス）。
/// </summary>
public sealed class InkAutoSave : IDisposable
{
    private readonly EngineSession _session;
    private readonly DispatcherQueueTimer _timer;
    private bool _disposed;
    private bool _pending;

    public InkAutoSave(EngineSession session, DispatcherQueue dispatcher)
    {
        _session = session;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            _pending = false;
            TrySave();
        };

        _session.StrokesChanged += OnStrokesChanged;
    }

    private void OnStrokesChanged()
    {
        if (_disposed)
            return;

        if (_session.ActiveStroke is not null)
            return;

        if (_session.IsPlaying)
            return;

        if (_session.Strokes.Count == 0)
        {
            Cancel();
            return;
        }

        Schedule();
    }

    private void Schedule()
    {
        _pending = true;
        _timer.Stop();
        _timer.Start();
    }

    public void Cancel()
    {
        _pending = false;
        _timer.Stop();
    }

    /// <summary>
    /// 再生開始前など、待ち時間を待たずにスナップショット保存してタイマー解除。
    /// </summary>
    public void FlushNow()
    {
        _timer.Stop();
        if (!_pending && _session.Strokes.Count == 0)
            return;

        _pending = false;
        TrySave();
    }

    private void TrySave()
    {
        if (_session.Strokes.Count == 0)
            return;

        var job = CreateJob();
        _ = SaveJobAsync(job);
    }

    private SaveJob CreateJob()
    {
        SaveFolderService.EnsureExists();

        var neta = string.IsNullOrWhiteSpace(_session.CurrentPath)
            ? "untitled"
            : Path.GetFileNameWithoutExtension(_session.CurrentPath);

        foreach (var c in Path.GetInvalidFileNameChars())
            neta = neta.Replace(c, '_');

        var pos = _session.TimelinePosition;
        var timeTag = $"{(int)pos.TotalMinutes:D2}m{pos.Seconds:D2}s{pos.Milliseconds / 100}";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{neta}_{timeTag}_{stamp}.png";
        var path = Path.Combine(SaveFolderService.FolderPath, fileName);

        return new SaveJob(
            _session.CurrentPath,
            pos,
            _session.Strokes.ToList(),
            path);
    }

    private static async Task SaveJobAsync(SaveJob job)
    {
        try
        {
            await OutputCompositeExporter.ExportAsync(
                job.VideoPath,
                job.Position,
                job.Strokes,
                job.OutPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InkAutoSave] {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session.StrokesChanged -= OnStrokesChanged;
        _timer.Stop();
    }

    private sealed record SaveJob(
        string? VideoPath,
        TimeSpan Position,
        IReadOnlyList<InkStrokeData> Strokes,
        string OutPath);
}
