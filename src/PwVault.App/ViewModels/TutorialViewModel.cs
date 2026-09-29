using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PwVault.App.ViewModels;

/// <summary>チュートリアルの 1 ページ。<paramref name="Settings"/> があれば「この設定を開く」を出す（アンロック中だけ）。</summary>
public sealed record TutorialStep(string Icon, string Title, string Body, SettingsCategory? Settings = null);

/// <summary>
/// 使い方の案内（画面の上に重ねて出す）。初回画面・保管庫を作った直後・「設定 → 使い方」から開く。
/// </summary>
public partial class TutorialViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public TutorialViewModel(MainViewModel main)
    {
        _main = main;
        Steps = BuildSteps(main.QuickUnlock?.Name);
    }

    public IReadOnlyList<TutorialStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFirst), nameof(IsLast), nameof(ProgressText), nameof(NextText),
        nameof(CanOpenSettings), nameof(Progress))]
    public partial int Index { get; set; }

    public TutorialStep Current => Steps[Index];
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Steps.Count - 1;
    public string ProgressText => $"{Index + 1} / {Steps.Count}";
    public double Progress => (Index + 1) * 100.0 / Steps.Count;
    public string NextText => IsFirst ? "はじめる" : IsLast ? "完了" : "次へ";

    /// <summary>「この設定を開く」を出すか（保管庫を開いているときだけ。初回画面では出さない）。</summary>
    public bool CanOpenSettings => Current.Settings is not null && _main.CurrentPage is VaultViewModel;

    [RelayCommand]
    private void Next()
    {
        if (IsLast) _main.CloseTutorial();
        else Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst) Index--;
    }

    [RelayCommand]
    private void Close() => _main.CloseTutorial();

    [RelayCommand]
    private void OpenSettings()
    {
        if (Current.Settings is not { } category || _main.CurrentPage is not VaultViewModel vault) return;
        _main.CloseTutorial();
        vault.ShowSettingsAt(category);
    }

    private static IReadOnlyList<TutorialStep> BuildSteps(string? quickUnlockName)
    {
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        var steps = new List<TutorialStep>
        {
            new("👋", "PwVault へようこそ",
                "PwVault は、パスワードを暗号化して、この PC の 1 つのファイルにまとめて保存するアプリです。\n" +
                "覚えるのは「マスターパスワード」1 つだけ。あとは PwVault が覚えます。\n\n" +
                "この案内では、よく使う操作を順番に紹介します（約 2 分）。あとから「設定 → 使い方」でも見られます。"),
            new("🔑", "マスターパスワードについて",
                "保管庫を開く鍵です。どこにも保存されず、忘れると保管庫は二度と開けません（復旧の手段はありません）。\n\n" +
                "・長め（12 文字以上）で、ほかでは使っていないものにする\n" +
                "・紙に書いて、安全な場所に保管する（初回画面の「緊急キット」が使えます）",
                SettingsCategory.Security),
            new("＋", "パスワードを登録する",
                $"一覧の上の「＋ 新規」（{mod}N）で、サイトのタイトル・ユーザー ID・パスワード・URL を登録します。\n\n" +
                $"パスワード欄の「生成」や、上の「生成」（{mod}G）で、推測されにくいパスワードを作れます。\n" +
                "ほかのアプリやブラウザからは「取込/書出」で CSV を取り込めます。"),
            new("🔍", "探す・使う",
                $"上の検索欄（{mod}F）に入力すると、すぐに絞り込めます。\n\n" +
                $"・ユーザー ID をコピー: {mod}B　・パスワードをコピー: {(OperatingSystem.IsMacOS() ? "⇧⌘C" : "Ctrl+Shift+C")}\n" +
                "・コピーした内容は、しばらくすると自動でクリップボードから消えます\n" +
                "・一覧をダブルクリックすると、そのサイトを開けます"),
            new("🛡", "パスワードの健康診断",
                "左上の絞り込みで「⚠ 弱いパスワード」「⚠ 使い回し」を選ぶと、見直した方がよいエントリが分かります。\n\n" +
                "パスワードを変えると、前のパスワードは「履歴」に残るので、変更の途中で失敗しても戻せます。\n" +
                "消したエントリは、しばらく「ゴミ箱」に残ります。"),
            new("🌐", "ブラウザで自動入力",
                "Chrome・Edge・Firefox の入力欄を右クリックして「PwVault」を選ぶと、そのサイトのユーザー ID とパスワードを入力できます。\n\n" +
                "使うには「設定 → ブラウザ連携」で有効にして、表示される手順でブラウザに拡張機能を入れてください。",
                SettingsCategory.Browser),
            new("☁", "ほかの PC と共有する",
                "Google ドライブ（パソコン版）や Nextcloud を使っていれば、「設定 → 保存先と同期」で保管庫をそちらに移せます。\n" +
                "2 台目の PC では、初回画面の「○○の保管庫を開く」から同じ保管庫を開けます。\n\n" +
                "複数の PC で同時に使っても、変更は自動でまとめられます。PwVault 自体は通信しません。",
                SettingsCategory.Storage),
        };
        if (quickUnlockName is not null)
            steps.Add(new("🙂", $"{quickUnlockName} ですばやく開く",
                $"「設定 → セキュリティ」で {quickUnlockName} を有効にすると、顔認証・指紋・PIN でアンロックできます。\n" +
                "忘れないよう、一定の日数ごとにマスターパスワードでのアンロックが必要です。",
                SettingsCategory.Security));
        steps.AddRange(
        [
            new("🔒", "ロックについて",
                $"しばらく操作しないと、自動でロックされます（時間は「設定 → 一般」で変更）。すぐにロックするには「🔒 ロック」（{mod}L）。\n" +
                "PC のロックやスリープでも、すぐにロックされます。",
                SettingsCategory.General),
            new("🎨", "見た目を変える",
                "一覧画面の右下のボタンで、ライト / ダークを切り替えられます。「設定 → 表示」では、ボタンなどの色も選べます。",
                SettingsCategory.Display),
            new("✅", "準備ができました",
                "困ったときは:\n" +
                "・保存のたびに、前の版を「.bak」として残しています（壊れたときは、ロック画面から復元できます）\n" +
                "・「取込/書出」から、暗号化したバックアップを別の場所に保存できます\n\n" +
                "この案内は、「設定 → 使い方」からいつでも見られます。"),
        ]);
        return steps;
    }
}
