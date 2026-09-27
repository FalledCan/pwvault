using Avalonia.Threading;
using Microsoft.Win32;

namespace PwVault.App.Services;

/// <summary>
/// 自動ロック（FR-03, T3）。無操作が一定時間続いたとき、OS のロック・スリープ・リモート切断時にロックを要求する。
/// Windows は OS のイベント、macOS は画面ロック状態の定期確認とスリープの検知で行う。
/// </summary>
public sealed class AutoLockService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    /// <summary>この間隔より長くタイマーが止まっていたら、スリープしていたとみなす。</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(60);
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public AutoLockService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => OnTick(DateTime.UtcNow);
        _timer.Start();

        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
    }

    public event EventHandler? LockRequested;

    /// <summary>アンロック中のみ true にする。</summary>
    public bool IsArmed { get; set; }

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public int IdleMinutes
    {
        get => (int)IdleTimeout.TotalMinutes;
        set => IdleTimeout = TimeSpan.FromMinutes(value);
    }

    /// <summary>キーボード・マウス操作があったときに呼ぶ。</summary>
    public void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    internal void CheckIdle()
    {
        if (IsArmed && DateTime.UtcNow - _lastActivityUtc >= IdleTimeout)
            RequestLock();
    }

    /// <summary>
    /// 5 秒ごとの確認。無操作に加えて、
    /// ・タイマーが長く止まっていた（スリープから復帰した）ならロック（どの OS でも。Windows は電源イベントでも検知）
    /// ・macOS は画面ロック中ならロック
    /// </summary>
    internal void OnTick(DateTime nowUtc)
    {
        var slept = nowUtc - _lastTickUtc > SleepGap;
        _lastTickUtc = nowUtc;
        if (slept || (OperatingSystem.IsMacOS() && MacSession.IsScreenLocked()))
        {
            RequestLock();
            return;
        }
        CheckIdle();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff)
            Dispatcher.UIThread.Post(RequestLock);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Dispatcher.UIThread.Post(RequestLock);
    }

    private void RequestLock()
    {
        if (!IsArmed) return;
        IsArmed = false;
        LockRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
