using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// ネタ一覧用の動画サムネイル・フレームレート読み込み。
/// </summary>
public static class VideoThumbnailLoader
{
    private const uint ThumbnailDecodeSize = 240;
    private const string FrameRateProperty = "System.Video.FrameRate";

    public static async Task LoadAsync(IReadOnlyList<NetaItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        using var gate = new SemaphoreSlim(3);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                cancellationToken.ThrowIfCancellationRequested();

                var frameLabel = await TryReadFrameRateLabelAsync(file, cancellationToken).ConfigureAwait(false);
                var stream = await TryOpenThumbnailStreamAsync(file, cancellationToken).ConfigureAwait(false);

                var dq = App.DispatcherQueue;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dq.TryEnqueue(async () =>
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(frameLabel))
                                item.FrameRateLabel = frameLabel;

                            if (stream is not null)
                            {
                                var bmp = new BitmapImage();
                                await bmp.SetSourceAsync(stream);
                                item.Thumbnail = bmp;
                            }

                            tcs.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                        finally
                        {
                            stream?.Dispose();
                        }
                    }))
                {
                    stream?.Dispose();
                    tcs.TrySetCanceled();
                }

                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Thumbnail] {item.Path}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 一覧差し替え時
        }
    }

    private static async Task<string?> TryReadFrameRateLabelAsync(
        StorageFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            var props = await file.Properties.RetrievePropertiesAsync([FrameRateProperty]);
            cancellationToken.ThrowIfCancellationRequested();
            if (!props.TryGetValue(FrameRateProperty, out var raw) || raw is null)
                return null;

            // System.Video.FrameRate は fps × 1000 の UInt32（例: 29970 → 29.97）
            double fpsTimes1000 = raw switch
            {
                uint u => u,
                int i => i,
                ulong ul => ul,
                long l => l,
                double d => d,
                float f => f,
                _ => 0
            };

            if (fpsTimes1000 <= 0)
                return null;

            var fps = fpsTimes1000 / 1000.0;
            if (fps < 1 || fps > 240)
                return null;

            return FormatFps(fps);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FrameRate] {ex.Message}");
            return null;
        }
    }

    private static string FormatFps(double fps)
    {
        var nearest = Math.Round(fps);
        if (Math.Abs(fps - nearest) < 0.05)
            return $"{(int)nearest}fps";

        return $"{fps:0.##}fps";
    }

    private static async Task<IRandomAccessStream?> TryOpenThumbnailStreamAsync(
        StorageFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.VideosView,
                ThumbnailDecodeSize,
                ThumbnailOptions.UseCurrentScale);
            if (thumb is not null && thumb.Size > 0)
                return await CopyToMemoryAsync(thumb, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail/shell] {ex.Message}");
        }

        try
        {
            return await TryOpenViaMediaCompositionAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail/media] {ex.Message}");
            return null;
        }
    }

    private static async Task<IRandomAccessStream?> TryOpenViaMediaCompositionAsync(
        StorageFile file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        using var thumbStream = await composition.GetThumbnailAsync(
            TimeSpan.Zero,
            (int)ThumbnailDecodeSize,
            (int)(ThumbnailDecodeSize * 9 / 16),
            VideoFramePrecision.NearestFrame);

        return await CopyToMemoryAsync(thumbStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IRandomAccessStream> CopyToMemoryAsync(
        IRandomAccessStream source,
        CancellationToken cancellationToken)
    {
        var memory = new InMemoryRandomAccessStream();
        await RandomAccessStream.CopyAsync(source, memory).AsTask(cancellationToken).ConfigureAwait(false);
        memory.Seek(0);
        return memory;
    }
}
