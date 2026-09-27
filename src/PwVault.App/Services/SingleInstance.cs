namespace PwVault.App.Services;

/// <summary>
/// 多重起動の防止と、2 つ目の起動を「既存のウィンドウを前に出す」合図に変える仕組み。
/// 通知領域に隠れているときに exe をもう一度起動しても、何も起きないように見えないようにする。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PwVault.SingleInstance";
    private const string ActivateEventName = @"Local\PwVault.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private bool _owned;

    /// <param name="waitForPrevious">
    /// 前のプロセスの終了を待つ時間（更新して再起動するとき、古いプロセスが終わるのを待つ）。0 なら待たない。
    /// </param>
    public SingleInstance(TimeSpan waitForPrevious = default)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _owned);
        if (!_owned && waitForPrevious > TimeSpan.Zero)
        {
            try { _owned = _mutex.WaitOne(waitForPrevious); }
            catch (AbandonedMutexException) { _owned = true; } // 前のプロセスが解放せずに終わった
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    public bool IsFirstInstance => _owned;

    /// <summary>先に起動している PwVault に「前に出て」と合図する。</summary>
    public void SignalFirstInstance() => _activate.Set();

    /// <summary>合図を受けたら <paramref name="onActivate"/> を呼ぶ（別スレッドから呼ばれる）。</summary>
    public void ListenForActivation(Action onActivate)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (_activate.WaitOne())
                    onActivate();
            }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true, Name = "PwVault.Activate" };
        thread.Start();
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // 別スレッドからの解放など。プロセス終了で解放される
            _owned = false;
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
