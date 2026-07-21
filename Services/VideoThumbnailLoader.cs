using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// ネタ一覧用の動画サムネイル読み込み。
/// </summary>
public static class VideoThumbnailLoader
{
    private const uint ThumbnailDecodeSize = 240;

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
                var stream = await TryOpenThumbnailStreamAsync(item.Path, cancellationToken).ConfigureAwait(false);
                if (stream is null)
                    return;

                var dq = App.DispatcherQueue;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dq.TryEnqueue(async () =>
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            await bmp.SetSourceAsync(stream);
                            item.Thumbnail = bmp;
                            tcs.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                        finally
                        {
                            stream.Dispose();
                        }
                    }))
                {
                    stream.Dispose();
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

    private static async Task<IRandomAccessStream?> TryOpenThumbnailStreamAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
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
            return await TryOpenViaMediaCompositionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Thumbnail/media] {ex.Message}");
            return null;
        }
    }

    private static async Task<IRandomAccessStream?> TryOpenViaMediaCompositionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
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
