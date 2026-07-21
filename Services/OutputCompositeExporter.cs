using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Editing;
using Windows.Storage;
using Kakikomi.Models;
using WuiColor = Windows.UI.Color;

namespace Kakikomi.Services;

/// <summary>
/// クリーン出力相当（映像フレーム + 書き込み）を 1920×1080 PNG で書き出す。
/// </summary>
public static class OutputCompositeExporter
{
    public static async Task ExportAsync(
        string? videoPath,
        TimeSpan position,
        IReadOnlyList<InkStrokeData> strokes,
        string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var bmp = new Bitmap(
            (int)EngineSession.DesignWidth,
            (int)EngineSession.DesignHeight,
            PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                try
                {
                    await DrawVideoFrameAsync(g, videoPath, position).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OutputComposite] frame: {ex}");
                }
            }

            DrawStrokes(g, strokes);
        }

        bmp.Save(filePath, ImageFormat.Png);
    }

    private static async Task DrawVideoFrameAsync(Graphics g, string videoPath, TimeSpan position)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var duration = clip.OriginalDuration;
        if (duration > TimeSpan.Zero)
        {
            if (position > duration)
                position = duration;
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
        }

        using var thumbStream = await composition.GetThumbnailAsync(
            position,
            (int)EngineSession.DesignWidth,
            (int)EngineSession.DesignHeight,
            VideoFramePrecision.NearestFrame);

        using var netStream = thumbStream.AsStreamForRead();
        using var frame = new Bitmap(netStream);

        var dest = ComputeUniformDest(
            frame.Width,
            frame.Height,
            (int)EngineSession.DesignWidth,
            (int)EngineSession.DesignHeight);
        g.DrawImage(frame, dest);
    }

    private static Rectangle ComputeUniformDest(int srcW, int srcH, int boxW, int boxH)
    {
        if (srcW <= 0 || srcH <= 0)
            return new Rectangle(0, 0, boxW, boxH);

        var scale = Math.Min(boxW / (double)srcW, boxH / (double)srcH);
        var w = Math.Max(1, (int)Math.Round(srcW * scale));
        var h = Math.Max(1, (int)Math.Round(srcH * scale));
        var x = (boxW - w) / 2;
        var y = (boxH - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    private static void DrawStrokes(Graphics g, IReadOnlyList<InkStrokeData> strokes)
    {
        foreach (var stroke in strokes)
        {
            if (stroke.Points.Count < 2)
                continue;

            using var pen = new Pen(ToDrawingColor(stroke.Color), (float)stroke.Thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var points = new PointF[stroke.Points.Count];
            for (var i = 0; i < stroke.Points.Count; i++)
            {
                var p = stroke.Points[i];
                points[i] = new PointF((float)p.X, (float)p.Y);
            }

            g.DrawLines(pen, points);
        }
    }

    private static Color ToDrawingColor(WuiColor color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);
}
