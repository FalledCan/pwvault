using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;

namespace PwVault.App.ViewModels;

/// <summary>読み取った QR コードをどう扱ったか。Done なら画面を閉じ、そうでなければ Message を出して読み取りを続ける。</summary>
public sealed record QrAccept(bool Done, string? Message = null);

/// <summary>
/// QR コードの読み取り画面（重ねて出す）。カメラに写すか、画像ファイル（スクリーンショット）から読む。
/// 読めた文字は accept に渡す。映像・画像は読み取りに使うだけで保存しない。
/// </summary>
public partial class QrScanViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;
    private readonly Func<string, QrAccept> _accept;
    private IAsyncDisposable? _camera;
    private int _decoding;
    private int _previewPending;
    private string? _lastText;
    private bool _closed;

    public QrScanViewModel(VaultViewModel owner, string heading, string hint, Func<string, QrAccept> accept)
    {
        _owner = owner;
        _accept = accept;
        Heading = heading;
        Hint = hint;
    }

    public string Heading { get; }
    public string Hint { get; }

    public bool CameraSupported => _owner.Main.Camera is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial WriteableBitmap? Preview { get; set; }

    public bool HasPreview => Preview is not null;

    [ObservableProperty]
    public partial bool IsCameraOn { get; set; }

    [ObservableProperty]
    public partial bool IsStarting { get; set; }

    /// <summary>読み取りの進み具合・案内。</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private async Task StartCameraAsync()
    {
        if (_owner.Main.Camera is not { } camera || IsCameraOn || IsStarting) return;
        Error = null;
        IsStarting = true;
        try
        {
            var session = await camera.StartAsync(OnFrame);
            if (_closed)
            {
                await session.DisposeAsync();
                return;
            }
            _camera = session;
            IsCameraOn = true;
        }
        catch (CameraException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsStarting = false;
        }
    }

    [RelayCommand]
    private Task StopCameraAsync() => StopCameraCoreAsync();

    private async Task StopCameraCoreAsync()
    {
        var camera = _camera;
        _camera = null;
        IsCameraOn = false;
        Preview = null;
        Array.Clear(_previewBuffers); // 描画中かもしれないので Dispose はせず、手放すだけ
        if (camera is not null) await camera.DisposeAsync();
    }

    /// <summary>カメラの 1 コマ（別スレッド）。読み取り中なら飛ばす。映像は画面の表示にだけ使う。</summary>
    internal void OnFrame(CameraFrame frame)
    {
        if (_closed) return;
        if (Interlocked.Exchange(ref _previewPending, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                ShowPreview(frame);
                Volatile.Write(ref _previewPending, 0);
            });

        if (Interlocked.Exchange(ref _decoding, 1) != 0) return;
        Task.Run(() =>
        {
            try
            {
                if (QrReader.Decode(frame.Bgra, frame.Width, frame.Height) is { } text)
                    Dispatcher.UIThread.Post(() => Handle(text));
            }
            finally
            {
                Volatile.Write(ref _decoding, 0);
            }
        });
    }

    /// <summary>
    /// 映像用の 2 枚の画像を交互に使う。Image は Source が同じ物のままだと中身を書き換えても描き直さない
    /// （最初の 1 コマで止まって見えた）ので、毎コマ別の物に替えて描き直させる。表示中の物には書き込まない。
    /// </summary>
    private readonly WriteableBitmap?[] _previewBuffers = new WriteableBitmap?[2];
    private int _previewIndex;

    private void ShowPreview(CameraFrame frame)
    {
        if (!IsCameraOn) return;
        _previewIndex ^= 1;
        var bitmap = _previewBuffers[_previewIndex];
        if (bitmap is null || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            bitmap?.Dispose(); // 表示中ではない方なので捨ててよい
            bitmap = _previewBuffers[_previewIndex] =
                new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var buffer = bitmap.Lock())
        {
            for (var y = 0; y < frame.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Bgra, y * frame.Width * 4, buffer.Address + y * buffer.RowBytes, frame.Width * 4);
        }
        Preview = bitmap;
    }

    [RelayCommand]
    private async Task OpenImageAsync()
    {
        Error = null;
        var path = await _owner.Main.FileDialogs.OpenFileAsync("QR コードの画像を選ぶ", "画像", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp");
        if (path is null) return;
        var text = await Task.Run(() => QrReader.DecodeFile(path, out var error) ?? (error is null ? null : "\0" + error));
        if (text is null) return;
        if (text.StartsWith('\0'))
        {
            Error = text[1..];
            return;
        }
        _lastText = null; // 同じ画像を選び直したときも扱う
        Handle(text);
    }

    /// <summary>読めた文字を渡す。同じ QR を写し続けても 1 回だけ扱う。</summary>
    internal void Handle(string text)
    {
        if (_closed || text == _lastText) return;
        _lastText = text;
        var result = _accept(text);
        if (result.Done)
        {
            _ = CloseAsync();
            return;
        }
        Error = null;
        Message = result.Message;
    }

    [RelayCommand]
    private Task CancelAsync() => CloseAsync();

    /// <summary>カメラを消して閉じる（ロックしたときも呼ぶ）。</summary>
    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _owner.CloseQrScan(this);
        await StopCameraCoreAsync();
    }
}
