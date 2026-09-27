using System.IO.Pipes;
using System.Security.Principal;

namespace PwVault.App.Services;

/// <summary>
/// 多重起動の防止と、2 つ目の起動を「既存のウィンドウを前に出す」合図に変える仕組み（Windows・macOS 共通）。
/// ・多重起動の判定は名前付き Mutex
/// ・「前に出して」の合図は、同じユーザーだけが接続できる名前付きパイプ（macOS では Unix ドメインソケット）
/// 通知領域に隠れているときに exe をもう一度起動しても、何も起きないように見えないようにする。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _owned;

    /// <param name="waitForPrevious">
    /// 前のプロセスの終了を待つ時間（更新して再起動するとき、古いプロセスが終わるのを待つ）。0 なら待たない。
    /// </param>
    /// <param name="name">テスト用に名前を変えるとき。</param>
    public SingleInstance(TimeSpan waitForPrevious = default, string? name = null)
    {
        var user = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName;
        name ??= "PwVault";
        _pipeName = $"{name}.Activate.{user}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.SingleInstance", out _owned);
        if (!_owned && waitForPrevious > TimeSpan.Zero)
        {
            try { _owned = _mutex.WaitOne(waitForPrevious); }
            catch (AbandonedMutexException) { _owned = true; } // 前のプロセスが解放せずに終わった
        }
    }

    public bool IsFirstInstance => _owned;

    /// <summary>先に起動している PwVault に「前に出て」と合図する。</summary>
    public void SignalFirstInstance()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            pipe.WriteByte(1);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // 相手がまだ起動途中などで受け取れない。何もしない
        }
    }

    /// <summary>合図を受けたら <paramref name="onActivate"/> を呼ぶ（別スレッドから呼ばれる）。</summary>
    public void ListenForActivation(Action onActivate)
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(ct);
                    var buffer = new byte[1];
                    if (await pipe.ReadAsync(buffer, ct) == 1)
                        onActivate();
                }
                catch (OperationCanceledException) { return; }
                catch (IOException)
                {
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // 別スレッドからの解放など。プロセス終了で解放される
            _owned = false;
        }
        _mutex.Dispose();
    }
}
