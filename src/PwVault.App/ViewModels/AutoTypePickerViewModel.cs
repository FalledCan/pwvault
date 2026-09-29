using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;
using PwVault.Core;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>自動タイプの選択窓の 1 行。</summary>
public sealed record AutoTypeItem(VaultEntry Entry, bool IsLinked)
{
    public Guid Id => Entry.Id;
    public string Title => Entry.Data.Title.Length > 0 ? Entry.Data.Title : "（無題）";
    public string Subtitle => Entry.Data.Username.Length > 0 ? Entry.Data.Username : Entry.Data.Url;
}

/// <summary>
/// 自動タイプの選択窓（ショートカットキーで出る）。入力先のアプリに紐付けたエントリを先頭に並べ、選ぶと元の画面に打ち込む。
/// 紐付けていないエントリを選んだときは「このアプリに入力しますか？」と確認する（間違った画面に打ち込まないため）。
/// 打ち込む直前にも、前面の画面が入力先のままか確かめ、変わっていたら打たない。
/// </summary>
public partial class AutoTypePickerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly VaultViewModel _vault;
    private readonly IAutoTypePlatform _platform;

    public AutoTypePickerViewModel(MainViewModel main, VaultViewModel vault, IAutoTypePlatform platform, TargetWindow target)
    {
        _main = main;
        _vault = vault;
        _platform = platform;
        Target = target;
        ApplyFilter();
    }

    public TargetWindow Target { get; }

    public string TargetText => Target.Title.Length > 0 ? $"{Target.ProcessName}（{Target.Title}）" : Target.ProcessName;

    public ObservableCollection<AutoTypeItem> Items { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial AutoTypeItem? SelectedItem { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>紐付けていないエントリを選んだときの確認中か。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    public partial AutoTypeItem? Confirming { get; set; }

    /// <summary>打ち込み中（選択窓は隠す）。</summary>
    [ObservableProperty] public partial bool IsTyping { get; set; }

    public string ConfirmText => Confirming is null ? "" :
        $"「{Confirming.Title}」を「{Target.ProcessName}」に入力します。\nこのアプリに紐付けると、次からは確認なしで候補の先頭に出ます。";

    public bool HasLinked => Items.Any(i => i.IsLinked);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var entries = _vault.Vault.GetEntries().Where(e => !e.Data.IsTrashed);
        var found = EntrySearch.Filter(entries.ToList(), SearchText, null, EntrySort.Title)
            .Select(e => new AutoTypeItem(e, AutoTypeMatcher.IsLinked(e.Data, Target.ProcessName)))
            .OrderByDescending(i => i.IsLinked)
            .ToList();
        Items.Clear();
        foreach (var item in found) Items.Add(item);
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasLinked));
    }

    /// <summary>選んだエントリを入力する（Enter・ダブルクリック・「入力」）。紐付けていなければ確認を出す。</summary>
    [RelayCommand]
    private Task ChooseAsync(AutoTypeItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return Task.CompletedTask;
        if (!item.IsLinked)
        {
            Confirming = item;
            return Task.CompletedTask;
        }
        return TypeAsync(item.Id);
    }

    [RelayCommand]
    private Task LinkAndTypeAsync()
    {
        if (Confirming is not { } item) return Task.CompletedTask;
        _vault.LinkAutoTypeApp(item.Id, Target.ProcessName);
        return TypeAsync(item.Id);
    }

    [RelayCommand]
    private Task TypeOnceAsync() => Confirming is { } item ? TypeAsync(item.Id) : Task.CompletedTask;

    [RelayCommand]
    private void CancelConfirm() => Confirming = null;

    /// <summary>検索欄で ↑↓ を押したとき、一覧の選択を動かす。</summary>
    [RelayCommand]
    private void MoveSelection(string? step)
    {
        if (Items.Count == 0 || !int.TryParse(step, out var d)) return;
        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[Math.Clamp(index + d, 0, Items.Count - 1)];
    }

    [RelayCommand]
    private void Cancel() => _main.CloseAutoTypePicker();

    /// <summary>元の画面に戻して打ち込む。戻せない・途中で前面が変わったら打たない（打ちかけでも止める）。</summary>
    private async Task TypeAsync(Guid id)
    {
        Confirming = null;
        Error = null;
        if (_vault.Vault.GetEntry(id) is not { } entry) return;
        var actions = AutoTypeMatcher.Sequence(entry.Data);
        if (actions.Count == 0)
        {
            Error = "このエントリには、入力するユーザー ID・パスワードがありません。";
            return;
        }

        IsTyping = true; // 選択窓を隠して、元の画面を前に戻す
        await Task.Delay(_main.AutoTypeDelay);
        if (!_platform.Activate(Target.Handle))
        {
            Fail("入力先の画面を前に戻せませんでした。");
            return;
        }
        await Task.Delay(_main.AutoTypeDelay);

        try
        {
            foreach (var action in actions)
            {
                if (!IsStillTarget())
                {
                    Fail("入力先の画面が切り替わったため、入力を止めました。");
                    return;
                }
                if (action is AutoTypeAction.Text t) _platform.TypeText(t.Value);
                else _platform.PressTab();
            }
        }
        catch (InvalidOperationException ex)
        {
            Fail(ex.Message);
            return;
        }

        _vault.Status = $"「{entry.Data.Title}」を {Target.ProcessName} に入力しました。";
        _main.CloseAutoTypePicker();
    }

    private bool IsStillTarget() =>
        _platform.GetForeground() is { } now && now.Handle == Target.Handle && now.ProcessId == Target.ProcessId;

    private void Fail(string message)
    {
        Error = message;
        IsTyping = false; // 選択窓を出し直して知らせる
    }
}
