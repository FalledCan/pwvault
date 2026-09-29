#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
#endif

namespace PwVault.App.Services;

/// <summary>カメラの 1 コマ（BGRA、1 画素 4 バイト、行の詰め物なし）。</summary>
public sealed record CameraFrame(byte[] Bgra, int Width, int Height);

/// <summary>カメラを使えなかった理由（利用者に見せる文）。</summary>
public sealed class CameraException(string message) : Exception(message);

/// <summary>
/// QR コードを写すためのカメラ。映像は PC の中で読み取りに使うだけで、保存・送信はしない。
/// テストでは偽物に差し替える（本物のカメラは点けない）。
/// </summary>
public interface ICameraSource
{
    /// <summary>
    /// カメラを点けて、コマが届くたびに onFrame を呼ぶ（UI スレッドとは別のスレッドから）。
    /// 返したものを Dispose するとカメラを消す。使えなければ <see cref="CameraException"/>。
    /// </summary>
    Task<IAsyncDisposable> StartAsync(Action<CameraFrame> onFrame);
}

public static class CameraSources
{
    /// <summary>この OS で使えるカメラ（今は Windows だけ。ほかは null）。</summary>
    public static ICameraSource? CreateDefault()
    {
#if WINDOWS
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? new WindowsCamera() : null;
#else
        return null;
#endif
    }
}

#if WINDOWS
/// <summary>Windows のカメラ（Windows.Media.Capture のフレーム読み取り）。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsCamera : ICameraSource
{
    public async Task<IAsyncDisposable> StartAsync(Action<CameraFrame> onFrame)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new CameraException("カメラの使用が許可されていません。Windows の「設定 → プライバシーとセキュリティ → カメラ」で、" +
                                      "「カメラへのアクセス」と「デスクトップ アプリにカメラへのアクセスを許可する」をオンにしてください。");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            capture.Dispose();
            throw new CameraException("カメラが見つからないか、ほかのアプリが使っています。");
        }

        try
        {
            var source = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault() ?? throw new CameraException("使えるカメラが見つかりませんでした。");

            // QR が読める程度の大きさで、重すぎないもの（横 1280 前後）を選ぶ
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width is >= 640 and <= 1920)
                .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - 1280))
                .FirstOrDefault();
            if (format is not null)
            {
                try { await source.SetFormatAsync(format); }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* 既定の大きさのまま使う */ }
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += (r, _) =>
            {
                try
                {
                    using var frame = r.TryAcquireLatestFrame();
                    if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap) return;
                    using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null
                        : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    var use = converted ?? bitmap;
                    var bytes = new byte[use.PixelWidth * use.PixelHeight * 4];
                    use.CopyToBuffer(bytes.AsBuffer());
                    onFrame(new CameraFrame(bytes, use.PixelWidth, use.PixelHeight));
                }
                catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
                {
                    // 止める途中のコマなど。次のコマを待つ
                }
            };
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                reader.Dispose();
                throw new CameraException("カメラを始められませんでした（ほかのアプリが使っているかもしれません）。");
            }
            return new Session(capture, reader);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private sealed class Session(MediaCapture capture, MediaFrameReader reader) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await reader.StopAsync(); }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException) { }
            reader.Dispose();
            capture.Dispose();
        }
    }
}
#endif
