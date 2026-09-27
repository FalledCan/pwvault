using Avalonia.Threading;
using Microsoft.Win32;

namespace PwVault.App.Services;

/// <summary>
/// 自動ロック（FR-03, T3）。無操作が一定時間続いたとき、OS のロック・スリープ・リモート切断時にロックを要求する。
/// </summary>
public sealed class AutoLockService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public AutoLockService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => CheckIdle();
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
