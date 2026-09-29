using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;
using PwVault.Core;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>自動タイプの選択窓の 1 行。</summary>
public sealed record AutoTypeItem(VaultEntry Entry, bool IsLinked, int? Number = null)
{
    public Guid Id => Entry.Id;

    /// <summary>このアプリ用のアカウントに付ける番号（1〜9。数字キーで選べる）。</summary>
    public bool HasNumber => Number is not null;
    public string NumberText => Number?.ToString() ?? "";
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

    /// <summary>入力先。アプリが開いたときに出した選択窓では、同じアプリの別の画面（起動画面 → ログイン画面など）に付け替える。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(CanIgnoreThisWindow))]
    public partial TargetWindow Target { get; set; }

    /// <summary>アプリが前に出たことで自動で出した選択窓か（そのときの説明を出す）。</summary>
    public bool OpenedAutomatically { get; init; }

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
        // このアプリ用のアカウント（複数アカウントなど）に 1〜9 の番号を付ける
        var number = 0;
        found = found.Select(i => i.IsLinked && number < 9 ? i with { Number = ++number } : i).ToList();

        Items.Clear();
        foreach (var item in found) Items.Add(item);
        // 前回このアプリに入力したアカウントを選んでおく（無ければ先頭）
        var last = _main.LastAutoTypeEntry(Target.ProcessName);
        SelectedItem = Items.FirstOrDefault(i => i.IsLinked && i.Id == last) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasLinked));
        OnPropertyChanged(nameof(LinkedCount));
    }

    /// <summary>2 つ以上なら true（番号で選べることの案内を出す）。</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter MoreThanOne =
        new Avalonia.Data.Converters.FuncValueConverter<int, bool>(n => n > 1);

    /// <summary>このアプリ用のアカウントの数（2 つ以上なら、番号で選べることを案内する）。</summary>
    public int LinkedCount => Items.Count(i => i.IsLinked);

    /// <summary>数字キー（検索欄が空のとき）で、その番号のアカウントを入力する。番号が無ければ false。</summary>
    public bool ChooseNumber(int number)
    {
        if (SearchText.Length > 0 || Items.FirstOrDefault(i => i.Number == number) is not { } item) return false;
        SelectedItem = item;
        ChooseCommand.Execute(item);
        return true;
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

    /// <summary>自動で出た候補で「この画面では出さない」。この題名の画面では次から出さない。</summary>
    [RelayCommand]
    private void IgnoreThisWindow()
    {
        _vault.IgnoreAutoTypeWindow(Target.ProcessName, Target.Title);
        _vault.Status = $"「{AutoTypeMatcher.StableTitlePart(Target.Title)}」の画面では、次から候補を出しません（エントリの編集画面で戻せます）。";
        _main.CloseAutoTypePicker();
    }

    public bool CanIgnoreThisWindow => OpenedAutomatically && Target.Title.Trim().Length > 0;

    /// <summary>元の画面に戻して打ち込む。戻せない・途中で前面が変わったら打たない（打ちかけでも止める）。</summary>
    private async Task TypeAsync(Guid id)
    {
        Confirming = null;
        Error = null;
        if (_vault.Vault.GetEntry(id) is not { } entry) return;
        if (AutoTypeMatcher.Sequence(entry.Data).Count == 0)
        {
            Error = "このエントリには、入力するユーザー ID・パスワードがありません。";
            return;
        }

        IsTyping = true; // 選択窓を隠して、元の画面を前に戻してから打つ
        var target = Target;
        if (await AutoTyper.TypeAsync(_platform, target, entry.Data, _main.AutoTypeDelay, activate: true, _main.Clock) is { } error)
        {
            Fail(error);
            return;
        }

        _vault.Status = $"「{entry.Data.Title}」を {target.ProcessName} に入力しました。";
        _main.RememberAutoTypeEntry(target.ProcessName, id);
        _main.CloseAutoTypePicker();
    }

    private void Fail(string message)
    {
        Error = message;
        IsTyping = false; // 選択窓を出し直して知らせる
    }
}
