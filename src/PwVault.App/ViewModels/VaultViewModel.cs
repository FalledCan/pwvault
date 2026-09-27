using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;
using PwVault.Core;
using PwVault.Core.Bridge;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

public enum FilterKind { All, Favorites, Weak, Reused, Tag }

public sealed record FilterOption(string Label, FilterKind Kind, string? Tag = null);

public sealed record SortOption(string Label, EntrySort Sort);

/// <summary>一覧の 1 行。</summary>
public sealed partial class EntryItemViewModel(VaultEntry entry, bool isWeak, bool isReused) : ObservableObject
{
    /// <summary>サイトのアイコン。無ければ頭文字の丸を表示する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial Bitmap? Icon { get; set; }

    public bool HasIcon => Icon is not null;
    public string? HostKey { get; } = UrlMatcher.HostKey(entry.Data.Url);
    public VaultEntry Entry { get; } = entry;
    public Guid Id => Entry.Id;
    public string Title => Entry.Data.Title.Length > 0 ? Entry.Data.Title : "（無題）";
    public string Subtitle => Entry.Data.Username.Length > 0 ? Entry.Data.Username : Entry.Data.Url;
    public string Initial => Title[..System.Globalization.StringInfo.GetNextTextElementLength(Title)].ToUpperInvariant();
    public bool IsFavorite => Entry.Data.Favorite;
    public bool IsWeak { get; } = isWeak;
    public bool IsReused { get; } = isReused;
    public bool HasWarning => IsWeak || IsReused;
    public string WarningText => (IsWeak, IsReused) switch
    {
        (true, true) => "弱いパスワード・使い回し",
        (true, false) => "弱いパスワード",
        (false, true) => "使い回し",
        _ => "",
    };
    public string TagsText => string.Join(", ", Entry.Data.Tags);
    public string CreatedText => Entry.Data.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string UpdatedText => Entry.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public bool HasUrl => Uri.TryCreate(Entry.Data.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
    public bool HasHistory => Entry.Data.History.Count > 0;
}

/// <summary>S3 一覧 / S4 詳細 / S5 編集 と、S6〜S9 への入口。</summary>
public partial class VaultViewModel : ViewModelBase
{
    private Vault? _vault;
    private HealthReport _health = HealthReport.Empty;

    public VaultViewModel(MainViewModel main, Vault vault)
    {
        Main = main;
        _vault = vault;
        Icons = new IconService(vault, main.IconFetcher);
        Icons.IconChanged += OnIconChanged;
        SelectedSort = SortOptions.FirstOrDefault(s => s.Sort == main.Settings.Sort) ?? SortOptions[0];
        main.Clipboard.CountdownChanged += OnClipboardCountdown;
        Refresh();
    }

    public MainViewModel Main { get; }

    public IconService Icons { get; }

    private void OnIconChanged(string host)
    {
        foreach (var item in Items.Where(i => i.HostKey == host))
            item.Icon = Icons.GetIcon(item.Entry.Data.Url);
    }

    /// <summary>設定でオンなら、アイコンの無いサイトから取得を始める。</summary>
    public void FetchIconsIfEnabled()
    {
        if (_vault is not null && Main.Settings.FetchSiteIcons)
            Icons.FetchMissing(_vault.GetEntries());
    }

    /// <summary>一覧のダブルクリック。設定でオンならサイトを開く。</summary>
    public void OnItemDoubleClicked()
    {
        if (Main.Settings.DoubleClickOpensUrl)
            OpenUrl();
    }
    public Vault Vault => _vault ?? throw new InvalidOperationException("保管庫はロックされています。");

    public bool IsUnlocked => _vault is { IsLocked: false };

    public ObservableCollection<EntryItemViewModel> Items { get; } = [];
    public ObservableCollection<FilterOption> Filters { get; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("タイトル順", EntrySort.Title),
        new("更新日時（新しい順）", EntrySort.UpdatedDesc),
        new("作成日時（新しい順）", EntrySort.CreatedDesc),
    ];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial FilterOption? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial EntryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial EntryEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubPage))]
    public partial ViewModelBase? SubPage { get; set; }

    [ObservableProperty]
    public partial GeneratorViewModel? Generator { get; set; }

    [ObservableProperty]
    public partial bool RevealPassword { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? ClipboardStatus { get; set; }

    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    public bool HasSelection => SelectedItem is not null;
    public bool IsEditing => Editor is not null;
    public bool HasSubPage => SubPage is not null;

    /// <summary>検索欄にフォーカスしてほしいとき（Ctrl+F）。</summary>
    public event EventHandler? FocusSearchRequested;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(FilterOption? value) => ApplyFilter();

    partial void OnSelectedSortChanged(SortOption value)
    {
        Main.Settings.Sort = value.Sort;
        Main.SaveSettings();
        ApplyFilter();
    }

    partial void OnSelectedItemChanged(EntryItemViewModel? value) => RevealPassword = false;

    // ------------------------------------------------------------------ 一覧の更新

    /// <summary>保管庫の内容から一覧・タグ・健全性レポートを作り直す。</summary>
    public void Refresh()
    {
        if (_vault is null) return;
        var entries = _vault.GetEntries();
        _health = PasswordHealth.Analyze(entries);

        var previousFilter = SelectedFilter;
        Filters.Clear();
        Filters.Add(new FilterOption("すべて", FilterKind.All));
        Filters.Add(new FilterOption("★ お気に入り", FilterKind.Favorites));
        Filters.Add(new FilterOption($"⚠ 弱いパスワード（{_health.Weak.Count}）", FilterKind.Weak));
        Filters.Add(new FilterOption($"⚠ 使い回し（{_health.Reused.Count}）", FilterKind.Reused));
        foreach (var tag in EntrySearch.AllTags(entries))
            Filters.Add(new FilterOption("# " + tag, FilterKind.Tag, tag));

        SelectedFilter = Filters.FirstOrDefault(f => f.Kind == previousFilter?.Kind && f.Tag == previousFilter?.Tag) ?? Filters[0];
        ApplyFilter();

        // 完全削除したエントリのアイコンは消し、新しく増えたサイトのアイコンは取りに行く
        Icons.Prune(entries);
        FetchIconsIfEnabled();
    }

    private void ApplyFilter()
    {
        if (_vault is null || SelectedSort is null) return;
        var filter = SelectedFilter ?? new FilterOption("", FilterKind.All);
        var selectedId = SelectedItem?.Id;

        var result = EntrySearch.Filter(_vault.GetEntries(), SearchText,
            filter.Kind == FilterKind.Tag ? filter.Tag : null, SelectedSort.Sort);
        result = filter.Kind switch
        {
            FilterKind.Favorites => result.Where(e => e.Data.Favorite).ToList(),
            FilterKind.Weak => result.Where(e => _health.Weak.Contains(e.Id)).ToList(),
            FilterKind.Reused => result.Where(e => _health.Reused.Contains(e.Id)).ToList(),
            _ => result,
        };

        Items.Clear();
        foreach (var e in result)
            Items.Add(new EntryItemViewModel(e, _health.Weak.Contains(e.Id), _health.Reused.Contains(e.Id))
            {
                Icon = Icons.GetIcon(e.Data.Url),
            });

        SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
        CountText = $"{Items.Count} 件";
    }

    private void Select(Guid id)
    {
        SelectedItem = Items.FirstOrDefault(i => i.Id == id);
        if (SelectedItem is null)
        {
            // 検索条件に合わず見えない場合は条件を解除して表示する
            SearchText = "";
            SelectedFilter = Filters.FirstOrDefault();
            SelectedItem = Items.FirstOrDefault(i => i.Id == id);
        }
    }

    // ------------------------------------------------------------------ 保存

    /// <summary>変更をすぐにファイルへ保存する。失敗しても保管庫はメモリ上に残るので再試行できる。</summary>
    public bool Persist()
    {
        try
        {
            Vault.Save(Main.Settings.BackupGenerations);
            return true;
        }
        catch (VaultException ex)
        {
            Status = ex.Message + "（変更はまだ保存されていません）";
            return false;
        }
    }

    // ------------------------------------------------------------------ エントリ操作

    [RelayCommand]
    private void NewEntry()
    {
        SubPage = null;
        Editor = new EntryEditorViewModel(this, null);
    }

    [RelayCommand]
    private void EditEntry()
    {
        if (SelectedItem is null) return;
        Editor = new EntryEditorViewModel(this, SelectedItem.Entry);
    }

    public void CommitEditor(Guid? id, EntryData data)
    {
        Guid savedId;
        if (id is { } existing)
        {
            Vault.UpdateEntry(existing, data);
            savedId = existing;
        }
        else
        {
            savedId = Vault.AddEntry(data);
        }

        Editor = null;
        if (Persist()) Status = "保存しました。";
        Refresh();
        Select(savedId);
    }

    public void CancelEditor() => Editor = null;

    [RelayCommand]
    private void TrashEntry()
    {
        if (SelectedItem is null) return;
        var title = SelectedItem.Title;
        Vault.MoveToTrash(SelectedItem.Id);
        if (Persist()) Status = $"「{title}」をゴミ箱に移動しました。";
        Refresh();
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedItem is null) return;
        var id = SelectedItem.Id;
        Vault.SetFavorite(id, !SelectedItem.IsFavorite);
        Persist();
        Refresh();
        Select(id);
    }

    [RelayCommand]
    private void ToggleReveal() => RevealPassword = !RevealPassword;

    [RelayCommand]
    private void CopyUsername()
    {
        if (SelectedItem is { } item && item.Entry.Data.Username.Length > 0)
            CopyToClipboard(item.Entry.Data.Username, "ユーザーID");
    }

    [RelayCommand]
    private void CopyPassword()
    {
        if (SelectedItem is { } item && item.Entry.Data.Password.Length > 0)
            CopyToClipboard(item.Entry.Data.Password, "パスワード");
    }

    [RelayCommand]
    private void CopyUrl()
    {
        if (SelectedItem is { } item && item.Entry.Data.Url.Length > 0)
            CopyToClipboard(item.Entry.Data.Url, "URL");
    }

    [RelayCommand]
    private void CopyHistory(PasswordHistoryItem? item)
    {
        if (item is not null)
            CopyToClipboard(item.Password, "以前のパスワード");
    }

    [RelayCommand]
    private void OpenUrl()
    {
        // http/https 以外（file: や任意のスキーム）は開かない
        if (SelectedItem is not { HasUrl: true } item) return;
        try
        {
            Main.OpenInBrowser(new Uri(item.Entry.Data.Url));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "URL を開けませんでした。";
        }
    }

    public void CopyToClipboard(string text, string label)
    {
        try
        {
            _copiedLabel = label; // Copy の中で最初のカウントダウン通知が来るので先に設定する
            Main.Clipboard.Copy(text, Main.Settings.ClipboardClearSeconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or OutOfMemoryException)
        {
            Status = "クリップボードにコピーできませんでした。";
        }
    }

    private string _copiedLabel = "";

    private void OnClipboardCountdown(int remaining) =>
        ClipboardStatus = remaining > 0 ? $"{_copiedLabel}をコピーしました（{remaining} 秒後に消去）" : null;

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ サブ画面

    [RelayCommand]
    private void ShowGenerator() => Generator = new GeneratorViewModel(this, accept: null);

    public void OpenGeneratorFor(Action<string> accept) => Generator = new GeneratorViewModel(this, accept);

    public void CloseGenerator() => Generator = null;

    [RelayCommand]
    private void ShowSettings() => OpenSubPage(new SettingsViewModel(this));

    [RelayCommand]
    private void ShowImportExport() => OpenSubPage(new ImportExportViewModel(this));

    [RelayCommand]
    private void ShowTrash() => OpenSubPage(new TrashViewModel(this));

    private void OpenSubPage(ViewModelBase page)
    {
        Editor = null;
        Status = null;
        SubPage = page;
    }

    [RelayCommand]
    public void CloseSubPage()
    {
        SubPage = null;
        Refresh();
    }

    [RelayCommand]
    private void Lock() => Main.Lock();

    /// <summary>ロック時の後始末。鍵を破棄し、画面上の復号データへの参照を消す。保管庫のパスを返す。</summary>
    public string Close()
    {
        Main.Clipboard.CountdownChanged -= OnClipboardCountdown;
        var path = _vault?.FilePath ?? Main.Settings.VaultPath ?? "";
        if (_vault is { IsDirty: true }) Persist();
        Icons.IconChanged -= OnIconChanged;
        Icons.Dispose(); // 保管庫鍵を捨てる前に、未保存のアイコンを暗号化して保存する
        _vault?.Dispose();
        _vault = null;
        Editor = null;
        Generator = null;
        SubPage = null;
        SelectedItem = null;
        Items.Clear();
        Filters.Clear();
        return path;
    }
}
