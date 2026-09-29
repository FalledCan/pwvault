using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;
using PwVault.Core;

namespace PwVault.App.ViewModels;

/// <summary>
/// 紐付けたアプリが開いたときの「数秒後に自動で入力」。画面の隅に残り秒数と「やめる」を出し、
/// 0 になったら、前面がまだそのアプリの画面なら打ち込む。待っている間にほかの画面に切り替えたら中止する。
/// 知らせる小さな窓は前面を奪わない（奪うと入力先が変わってしまう）。
/// </summary>
public partial class AutoTypeCountdownViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly VaultViewModel _vault;
    private readonly IAutoTypePlatform _platform;

    public AutoTypeCountdownViewModel(MainViewModel main, VaultViewModel vault, IAutoTypePlatform platform,
        TargetWindow target, VaultEntry entry, int seconds)
    {
        _main = main;
        _vault = vault;
        _platform = platform;
        Target = target;
        EntryId = entry.Id;
        EntryTitle = entry.Data.Title.Length > 0 ? entry.Data.Title : "（無題）";
        Remaining = seconds;
    }

    public TargetWindow Target { get; private set; }
    public Guid EntryId { get; }
    public string EntryTitle { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    public partial int Remaining { get; set; }

    public string Message => $"{Remaining} 秒後に「{EntryTitle}」を {Target.ProcessName} に入力します";

    /// <summary>同じアプリの別の画面（起動画面 → ログイン画面など）に移ったら、そちらに入力し直す（待ち時間も数え直す）。</summary>
    public void Retarget(TargetWindow target, int seconds)
    {
        Target = target;
        Remaining = seconds;
    }

    /// <summary>1 秒ごとに呼ぶ。0 になったら打ち込む。</summary>
    public async Task TickAsync()
    {
        if (!AutoTyper.IsTarget(_platform.GetForeground(), Target))
        {
            Stop($"{Target.ProcessName} から切り替わったため、自動入力をやめました。");
            return;
        }
        if (--Remaining > 0) return;

        if (_vault.Vault.GetEntry(EntryId) is not { } entry)
        {
            Stop(null);
            return;
        }
        var error = await AutoTyper.TypeAsync(_platform, Target, entry.Data, TimeSpan.Zero, activate: false);
        if (error is null) _main.RememberAutoTypeEntry(Target.ProcessName, EntryId);
        Stop(error ?? $"「{entry.Data.Title}」を {Target.ProcessName} に入力しました。");
    }

    [RelayCommand]
    private void Cancel() => Stop("自動入力をやめました。");

    public bool CanIgnoreThisWindow => Target.Title.Trim().Length > 0;

    /// <summary>「この画面では出さない」。この題名の画面では次から自動入力しない。</summary>
    [RelayCommand]
    private void IgnoreThisWindow()
    {
        _vault.IgnoreAutoTypeWindow(Target.ProcessName, Target.Title);
        Stop($"「{Target.Title}」の画面では、次から自動入力しません（エントリの編集画面で戻せます）。");
    }

    private void Stop(string? status)
    {
        if (status is not null) _vault.Status = status;
        _main.CloseAutoTypeCountdown();
    }
}
