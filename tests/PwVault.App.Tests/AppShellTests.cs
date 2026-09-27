using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform;
using PwVault.App.Services;

namespace PwVault.App.Tests;

/// <summary>起動まわり（多重起動の合図・通知領域のアイコン・OS ごとの違い）。</summary>
public class AppShellTests
{
    // 本物の PwVault と衝突しないよう、テストごとに別の名前を使う
    private static string TestName() => "PwVaultTest" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void SecondInstance_SignalsFirst()
    {
        var name = TestName();
        using var first = new SingleInstance(name: name);
        Assert.True(first.IsFirstInstance);
        using var activated = new ManualResetEventSlim();
        first.ListenForActivation(activated.Set);

        // 別スレッドから 2 つ目を作る（Mutex は同じスレッドだと再入できてしまうため）
        var secondWasFirst = true;
        var t = new Thread(() =>
        {
            using var second = new SingleInstance(name: name);
            secondWasFirst = second.IsFirstInstance;
            if (!second.IsFirstInstance) second.SignalFirstInstance();
        });
        t.Start();
        t.Join();

        Assert.False(secondWasFirst);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AfterUpdate_WaitsForPreviousProcessToExit()
    {
        var name = TestName();

        // 古いプロセス役: 所有したまま 0.5 秒後に手放す
        using var owned = new ManualResetEventSlim();
        var old = new Thread(() =>
        {
            using var first = new SingleInstance(name: name);
            owned.Set();
            Thread.Sleep(500);
        });
        old.Start();
        owned.Wait(TestContext.Current.CancellationToken);

        // 新しいプロセス役: 待たなければ 2 つ目扱い、待てば 1 つ目になれる
        var noWait = true;
        var t = new Thread(() => { using var s = new SingleInstance(name: name); noWait = s.IsFirstInstance; });
        t.Start();
        t.Join();
        Assert.False(noWait);

        var withWait = false;
        t = new Thread(() => { using var s = new SingleInstance(TimeSpan.FromSeconds(10), name); withWait = s.IsFirstInstance; });
        t.Start();
        t.Join();
        old.Join();
        Assert.True(withWait);
    }

#if WINDOWS
    [AvaloniaFact]
    public async Task WindowsHello_AvailabilityCheck_Works()
    {
        // 「使えるか」の確認は確認画面を出さない。例外にならず、どちらかの答えが返ること
        var provider = QuickUnlockProviders.CreateDefault();
        Assert.IsType<WindowsHelloProvider>(provider);
        var available = await provider.IsAvailableAsync();
        TestContext.Current.TestOutputHelper?.WriteLine($"Windows Hello available: {available}");
    }
#endif

    [AvaloniaFact]
    public void TrayIconAsset_Loads()
    {
        // 起動時に通知領域のアイコンを作るのに使う資源が、アプリの中から読めること
        using var stream = AssetLoader.Open(new Uri("avares://PwVault/Assets/avalonia-logo.ico"));
        var icon = new WindowIcon(stream);
        Assert.NotNull(icon);
    }

    [AvaloniaFact]
    public void Shortcuts_UseCommandKeyOnMac()
    {
        var control = new Border();
        control.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control) });
        control.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) });
        control.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape) });
        Shortcuts.AdaptForPlatform(control);

        var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.Equal(mod, control.KeyBindings[0].Gesture!.KeyModifiers);
        Assert.Equal(mod | KeyModifiers.Shift, control.KeyBindings[1].Gesture!.KeyModifiers);
        Assert.Equal(KeyModifiers.None, control.KeyBindings[2].Gesture!.KeyModifiers);
        Assert.Equal(OperatingSystem.IsMacOS() ? "⇧⌘C" : "Ctrl+Shift+C", Shortcuts.CopyPassword);
    }

    [AvaloniaFact]
    public void AutoLock_LocksAfterSleepGap()
    {
        using var autoLock = new AutoLockService { IsArmed = true, IdleTimeout = TimeSpan.FromHours(1) };
        var locked = 0;
        autoLock.LockRequested += (_, _) => locked++;
        var now = DateTime.UtcNow;

        autoLock.OnTick(now.AddSeconds(5));
        Assert.Equal(0, locked);           // 普通の間隔ではロックしない
        autoLock.OnTick(now.AddMinutes(30));
        Assert.Equal(1, locked);           // タイマーが長く止まっていた＝スリープしていた
    }
}
