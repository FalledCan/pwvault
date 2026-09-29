using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
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

        // 同期フォルダで他の端末と共有しているとき、その変更を取り込む（アンロック直後と、一定間隔ごと）
        SyncNow();
        _syncTimer = new DispatcherTimer { Interval = main.VaultSyncInterval };
        _syncTimer.Tick += (_, _) => SyncNow();
        _syncTimer.Start();
    }

    public MainViewModel Main { get; }

    public IconService Icons { get; private set; }

    private readonly DispatcherTimer _syncTimer;
    private string? _lastSyncError;
    private int _seenMergeCount;

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
            // ほかの端末でマスターパスワードが変わっていた: この操作が終わってからロックする（変更は退避される）
            if (ex.Kind is VaultErrorKind.HeaderChanged)
                Dispatcher.UIThread.Post(LockForHeaderChange);
            return false;
        }
    }

    /// <summary>
    /// ファイル側のマスターパスワード（鍵の設定）が変わっていたのでロックする。自動では受け入れない
    /// （攻撃者が古い・漏れたパスワードに戻した場合に、黙って受け入れないため）。未保存の変更はロック時に退避される。
    /// </summary>
    private void LockForHeaderChange()
    {
        if (Main.CurrentPage != this) return; // もうロック済み
        Main.AddLockNotice(
            "ほかの端末でマスターパスワード（または鍵の設定）が変更されたため、ロックしました。新しいマスターパスワードでアンロックしてください。" +
            "\n心当たりがない場合は、保管庫ファイルが書き換えられた可能性があります。アンロックせずに、保管庫の場所を確かめて、バックアップ（.bak）から戻すことを検討してください。");
        Main.Lock();
    }

    /// <summary>
    /// ロックする時点で保存できなかった変更を、暗号化したまま別名のファイルに退避する（メモリごと捨てないため）。
    /// 名前は同期アプリの競合コピーと同じ形（「元の名前 (…).pwv」）にするので、同じ保管庫なら次のアンロックで自動で取り込まれる。
    /// 保管庫のフォルダに書けなければ、この PC のデータフォルダに書く。場所はロック画面で案内する。
    /// </summary>
    private void RescueUnsaved(Vault vault)
    {
        var name = $"{Path.GetFileNameWithoutExtension(vault.FilePath)} (保存できなかった変更 {DateTime.Now:yyyy-MM-dd HHmmss}){Path.GetExtension(vault.FilePath)}";
        foreach (var dir in new[] { Path.GetDirectoryName(vault.FilePath)!, Path.Combine(Main.LocalDataDir, "Unsaved") })
        {
            var rescue = Path.Combine(dir, name);
            try
            {
                vault.SaveCopyTo(rescue);
                Main.AddLockNotice($"保存できなかった変更を、次のファイルに退避しました（暗号化済み）:\n{rescue}\n同じ保管庫なら、次にアンロックしたときに自動で取り込みます。");
                return;
            }
            catch (VaultException) { }
        }
        Main.AddLockNotice("保存できなかった変更があり、退避もできませんでした。");
    }

    // ------------------------------------------------------------------ 他の端末との同期（同期フォルダ）

    /// <summary>
    /// 他の端末が保管庫ファイルを書き換えていたら取り込み、同期アプリの競合コピーがあれば合体して消す。
    /// こちらにしかない変更（前回保存に失敗したものを含む）があれば書き戻す。取り込んだら一覧を更新する。
    /// </summary>
    public void SyncNow()
    {
        if (_vault is not { IsLocked: false } vault) return;
        try
        {
            var copies = vault.MergeConflictCopies();
            vault.SyncFromDisk();
            if (vault.IsDirty && !Persist()) return;
            // 保存の直前の取り込み（Persist の中）で変わった分も含めて、前回から取り込みがあったか
            var changed = vault.MergeCount != _seenMergeCount || copies.Count > 0;
            _seenMergeCount = vault.MergeCount;
            foreach (var copy in copies)
            {
                try { File.Delete(copy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _lastSyncError = null;
            if (changed)
            {
                Refresh();
                if (SubPage is TrashViewModel trash) trash.Reload(); // ゴミ箱は自前の一覧を持っている
                Status = "他の端末での変更を取り込みました。";
            }
        }
        catch (VaultException ex) when (ex.Kind is VaultErrorKind.Io or VaultErrorKind.Corrupted or VaultErrorKind.InvalidFormat)
        {
            // 同期アプリが書き込み中などで読めない。次の確認でやり直す
        }
        catch (VaultException ex) when (ex.Kind is VaultErrorKind.HeaderChanged)
        {
            LockForHeaderChange();
        }
        catch (VaultException ex)
        {
            // 改ざん・別の保管庫・新しい形式など。同じ内容を何度も出さない
            var message = "保管庫ファイルの変更を取り込めませんでした: " + ex.Message;
            if (message != _lastSyncError) Status = _lastSyncError = message;
        }
    }

    /// <summary>
    /// 保管庫を別の場所へ移す（保存先の切り替え）。移動先に同じ保管庫があれば合体する。
    /// アイコンのキャッシュと世代バックアップも移す。元が「この PC」なら元のファイルを消し、
    /// 同期フォルダなど他の場所からの移動なら、ほかの端末が使っているかもしれないので残す。
    /// 移動先に別の保管庫があれば、そちらを開くか確認する。結果の説明を返す。
    /// </summary>
    public async Task<string> MoveVaultToAsync(string target)
    {
        var vault = Vault;
        var oldPath = vault.FilePath;
        target = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(oldPath, target, comparison)) return "すでにこの場所に保存しています。";

        if (File.Exists(target))
        {
            Guid otherId;
            try { otherId = Core.Format.VaultFileCodec.Deserialize(File.ReadAllBytes(target)).Header.VaultId; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
            {
                return "移動先に読めないファイル（" + Path.GetFileName(target) + "）があるため、移動しませんでした。";
            }
            if (otherId != vault.VaultId)
            {
                if (!await Main.ConfirmAsync("移動先に別の保管庫があります",
                        "移動先には別の保管庫があります。今の保管庫はそのまま残し、移動先の保管庫を開きますか？\n（開くには、その保管庫のマスターパスワードが必要です）",
                        "移動先の保管庫を開く"))
                    return "移動をやめました。";
                Main.Lock();
                Main.ShowUnlock(target);
                return "移動先の保管庫を開きます。";
            }
        }

        var keepOriginal = Main.SyncFolders.KindOf(oldPath) != StorageKind.Local;
        Icons.IconChanged -= OnIconChanged;
        Icons.Dispose(); // 未保存のアイコンを元の場所に書いてから移す
        try
        {
            vault.MoveTo(target, Main.Settings.BackupGenerations);
        }
        catch (VaultException ex)
        {
            RecreateIcons();
            return "移動できませんでした: " + ex.Message;
        }

        MoveOrCopy(Core.Icons.IconCache.PathFor(oldPath), Core.Icons.IconCache.PathFor(target), keepOriginal);
        var backups = Core.Storage.AtomicFileStore.ListBackups(oldPath);
        if (Core.Storage.AtomicFileStore.ListBackups(target).Count == 0)
            for (var i = 0; i < backups.Count; i++)
                MoveOrCopy(backups[i], Core.Storage.AtomicFileStore.BackupPath(target, i + 1), keepOriginal);
        if (!keepOriginal)
        {
            try { File.Delete(oldPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        RecreateIcons();
        Main.Settings.VaultPath = target;
        Main.SaveSettings();
        Refresh();
        var where = SyncFolderLocator.DisplayName(Main.SyncFolders.KindOf(target));
        return $"保管庫を「{where}」に移しました。" + (keepOriginal ? "\n元の場所のファイルは、ほかの端末が使っているかもしれないので残してあります。" : "");
    }

    private void RecreateIcons()
    {
        Icons = new IconService(Vault, Main.IconFetcher);
        Icons.IconChanged += OnIconChanged;
    }

    private static void MoveOrCopy(string source, string dest, bool copy)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(dest)) return;
            if (copy) File.Copy(source, dest);
            else File.Move(source, dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 付随ファイルは無くても困らない */ }
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
        var recreated = false;
        if (id is { } existing && Vault.GetEntry(existing) is not null)
        {
            Vault.UpdateEntry(existing, data);
            savedId = existing;
        }
        else
        {
            // 編集している間に、ほかの端末で完全削除されていたら、新しいエントリとして残す
            recreated = id is not null;
            savedId = Vault.AddEntry(data);
        }

        Editor = null;
        if (Persist()) Status = recreated ? "編集中にほかの端末で削除されていたため、新しいエントリとして保存しました。" : "保存しました。";
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
        _syncTimer.Stop();
        Main.Clipboard.CountdownChanged -= OnClipboardCountdown;
        var path = _vault?.FilePath ?? Main.Settings.VaultPath ?? "";
        if (_vault is { IsDirty: true } && !Persist()) RescueUnsaved(_vault);
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
