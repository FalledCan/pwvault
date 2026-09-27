using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using PwVault.App.Services;

namespace PwVault.App.Tests;

/// <summary>起動まわり（多重起動の合図・通知領域のアイコン）。</summary>
public class AppShellTests
{
    [Fact]
    public void SecondInstance_SignalsFirst()
    {
        // 本物の PwVault と衝突しないよう、テストでは名前を変えられない代わりに「実行中でないこと」を前提にする
        Assert.SkipWhen(System.Diagnostics.Process.GetProcessesByName("PwVault").Length > 0, "PwVault が起動中");

        using var first = new SingleInstance();
        Assert.True(first.IsFirstInstance);
        using var activated = new ManualResetEventSlim();
        first.ListenForActivation(activated.Set);

        // 別スレッドから 2 つ目を作る（Mutex は同じスレッドだと再入できてしまうため）
        var secondWasFirst = true;
        var t = new Thread(() =>
        {
            using var second = new SingleInstance();
            secondWasFirst = second.IsFirstInstance;
            if (!second.IsFirstInstance) second.SignalFirstInstance();
        });
        t.Start();
        t.Join();

        Assert.False(secondWasFirst);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public void TrayIconAsset_Loads()
    {
        // 起動時に通知領域のアイコンを作るのに使う資源が、exe の中から読めること
        using var stream = AssetLoader.Open(new Uri("avares://PwVault/Assets/pwvault.ico"));
        var icon = new WindowIcon(stream);
        Assert.NotNull(icon);
    }
}
