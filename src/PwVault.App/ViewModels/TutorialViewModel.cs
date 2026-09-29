using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PwVault.App.ViewModels;

/// <summary>
/// チュートリアルの 1 ページ。<paramref name="Target"/> は照らす部品の目印（<c>Views.Tour.Id</c>）。null なら画面中央に説明だけを出す。
/// <paramref name="Prepare"/> はそのページを見せる前に画面を整える処理（設定を開く、エントリを選ぶなど）。戻ったときにも毎回呼ぶ。
/// </summary>
public sealed record TutorialStep(string? Target, string Icon, string Title, string Body, Action? Prepare = null);

/// <summary>
/// 実際の画面の部品を順番に照らして案内するチュートリアル。
/// 初回画面では保管庫の作り方を、保管庫を開いているときは一覧・設定などの使い方を案内する。
/// 開き方: 初回画面の「使い方を見る」、保管庫を作った直後（初回だけ自動）、「設定 → 使い方」。
/// </summary>
public partial class TutorialViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly VaultViewModel? _vault;

    public TutorialViewModel(MainViewModel main)
    {
        _main = main;
        _vault = main.CurrentPage as VaultViewModel;
        Steps = _vault is not null ? BuildVaultSteps(_vault, main) : BuildSetupSteps(main.CurrentPage as SetupViewModel);
        Steps[0].Prepare?.Invoke();
    }

    public IReadOnlyList<TutorialStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFirst), nameof(IsLast), nameof(ProgressText), nameof(NextText), nameof(Progress))]
    public partial int Index { get; set; }

    partial void OnIndexChanged(int value) => Steps[value].Prepare?.Invoke();

    public TutorialStep Current => Steps[Index];
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Steps.Count - 1;
    public string ProgressText => $"{Index + 1} / {Steps.Count}";
    public double Progress => (Index + 1) * 100.0 / Steps.Count;
    public string NextText => IsFirst ? "はじめる" : IsLast ? "完了" : "次へ →";

    [RelayCommand]
    private void Next()
    {
        if (IsLast) Close();
        else Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst) Index--;
    }

    /// <summary>終わる（途中でやめるときも）。案内のために開いた設定などは閉じて、一覧に戻す。</summary>
    [RelayCommand]
    private void Close()
    {
        if (_vault is { IsUnlocked: true } && _vault.SubPage is SettingsViewModel)
            _vault.CloseSubPage();
        _main.CloseTutorial(markSeen: _vault is not null);
    }

    // ------------------------------------------------------------------ ページ

    private static string Mod => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private static IReadOnlyList<TutorialStep> BuildVaultSteps(VaultViewModel vault, MainViewModel main)
    {
        // 一覧の画面に戻す（編集中・設定などを閉じる）
        void List()
        {
            vault.Editor = null;
            if (vault.HasSubPage) vault.CloseSubPage();
        }
        void Settings(SettingsCategory category)
        {
            vault.Editor = null;
            if (vault.SubPage is SettingsViewModel s) s.ShowCategory(category);
            else vault.ShowSettingsAt(category);
        }

        var steps = new List<TutorialStep>
        {
            new(null, "👋", "PwVault へようこそ",
                "実際の画面で、よく使うところを順番に案内します（約 2 分）。\n照らされた部分を見ながら「次へ」で進んでください。途中でやめても、「設定 → 使い方」からいつでも見られます。",
                List),
            new("new", "＋", "パスワードを登録する",
                $"ここから、サイトのタイトル・ユーザー ID・パスワード・URL を登録します（{Mod}N）。\n編集画面の「生成」で、推測されにくいパスワードを作れます。",
                List),
            new("search", "🔍", "探す",
                $"入力すると、タイトル・ユーザー ID・URL・タグからすぐに絞り込みます（{Mod}F）。",
                List),
            new("list", "📋", "一覧",
                "登録したエントリが並びます。選ぶと右に詳しい内容が出ます。ダブルクリックでそのサイトを開きます。",
                List),
        };

        if (vault.Items.Count > 0)
        {
            steps.Add(new("detail-password", "📎", "コピーして使う",
                $"「コピー」でパスワードをコピーできます（{(OperatingSystem.IsMacOS() ? "⇧⌘C" : "Ctrl+Shift+C")}。ユーザー ID は {Mod}B）。\nコピーした内容は、しばらくすると自動でクリップボードから消えます。",
                () =>
                {
                    List();
                    vault.SelectedItem ??= vault.Items.FirstOrDefault();
                }));
        }

        steps.AddRange(
        [
            new("filter", "🛡", "パスワードの健康診断",
                "ここで「⚠ 弱いパスワード」「⚠ 使い回し」を選ぶと、見直した方がよいエントリだけを表示します。タグやお気に入りでも絞り込めます。",
                List),
            new("generate", "🎲", "パスワードの生成",
                $"長さや文字の種類を選んで、推測されにくいパスワードを作ります（{Mod}G）。",
                List),
            new("import", "📥", "取り込みとバックアップ",
                "ほかのアプリやブラウザのパスワード（CSV）を取り込めます。暗号化したバックアップを別の場所に保存することもできます。",
                List),
            new("trash", "🗑", "ゴミ箱",
                "消したエントリはしばらくここに残り、元に戻せます。",
                List),
            new("settings", "⚙", "設定",
                "自動ロックの時間、見た目、保存先、ブラウザ連携などはここで変えます。続けて中を案内します。",
                List),
            new("settings-categories", "🗂", "設定のカテゴリー",
                "左のカテゴリーを選ぶと、その設定だけが右に出ます。",
                () => Settings(SettingsCategory.General)),
            new("settings-browser", "🌐", "ブラウザで自動入力",
                "ここで「有効にする」を押し、表示される手順で拡張機能を入れると、Chrome・Edge・Firefox の入力欄の右クリックから入力できます。",
                () => Settings(SettingsCategory.Browser)),
            new("settings-storage", "☁", "ほかの PC と共有",
                "Google ドライブや Nextcloud のフォルダに保管庫を移すと、ほかの PC でも同じ保管庫を使えます。PwVault 自体は通信しません。",
                () => Settings(SettingsCategory.Storage)),
        ]);

        if (main.AutoTypeSupported)
        {
            steps.Add(new("settings-autotype", "⌨", "ゲーム・アプリへの自動入力",
                "FF14 のランチャーなど、ブラウザ以外のログイン画面にも入力できます。ここで有効にして、ログイン画面でショートカットキーを押してください。",
                () => Settings(SettingsCategory.AutoType)));
        }

        if (main.QuickUnlock is { } hello)
        {
            steps.Add(new("settings-hello", "🙂", $"{hello.Name} ですばやく開く",
                $"ここで有効にすると、顔認証・指紋・PIN でアンロックできます。",
                () => Settings(SettingsCategory.Security)));
        }

        steps.AddRange(
        [
            new("lock", "🔒", "ロック",
                $"すぐにロックします（{Mod}L）。しばらく操作しないときや、PC のロック・スリープでも自動でロックされます。",
                List),
            new("theme", "🎨", "見た目",
                "ライトとダークを切り替えます。ボタンなどの色は「設定 → 表示」で選べます。",
                List),
            new(null, "✅", "準備ができました",
                "保存のたびに前の版を「.bak」として残しているので、ファイルが壊れてもロック画面から戻せます。\nこの案内は「設定 → 使い方」からいつでも見られます。",
                List),
        ]);
        return steps;
    }

    private static IReadOnlyList<TutorialStep> BuildSetupSteps(SetupViewModel? setup)
    {
        var steps = new List<TutorialStep>
        {
            new(null, "👋", "PwVault へようこそ",
                "PwVault は、パスワードを暗号化して、この PC の 1 つのファイルにまとめて保存するアプリです。覚えるのは「マスターパスワード」1 つだけです。\nまず、この画面で保管庫を作ります。"),
        };
        if (setup is { HasCloudVaults: true })
        {
            steps.Add(new("setup-cloud", "☁", "ほかの PC の保管庫",
                "ほかの PC で作った保管庫が同期フォルダにあります。同じ保管庫を使うなら、新しく作らずにこちらを開いてください。"));
        }
        steps.AddRange(
        [
            new("setup-path", "📁", "保存先",
                "保管庫のファイルを置く場所です。ふつうはこのままで大丈夫です。あとから「設定 → 保存先と同期」で Google ドライブなどに移せます。"),
            new("setup-password", "🔑", "マスターパスワード",
                "保管庫を開く鍵です。12 文字以上で、ほかでは使っていないものにしてください。\nどこにも保存されず、忘れると保管庫は二度と開けません。"),
            new("setup-kit", "📝", "緊急キット",
                "マスターパスワードを書き留めておく用紙です。印刷して手書きで記入し、安全な場所に保管してください。"),
            new("setup-open", "📂", "既存の保管庫を開く",
                "前に作った保管庫のファイルがあれば、ここから開きます。"),
            new("setup-create", "✨", "作成",
                "入力できたら「作成」を押します。作成したあと、一覧の画面の使い方も案内します。"),
        ]);
        return steps;
    }
}
