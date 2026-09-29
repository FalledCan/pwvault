using System.Text;
using Avalonia.Headless.XUnit;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;
using PwVault.Core.Otp;

namespace PwVault.App.Tests;

/// <summary>
/// ワンタイムパスワード（2 段階認証）: 登録・表示・QR コードの読み取り（偽のカメラ・画像）・Google Authenticator からの移行・自動入力。
/// </summary>
public class TotpUiTests
{
    private const string Master = "correct horse battery staple";

    /// <summary>RFC 6238 のテスト用のキー。1111111109 秒の 6 桁は 081804。</summary>
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Secret2 = Encoding.ASCII.GetBytes("abcdefghijabcdefghij");
    private static readonly byte[] Secret3 = Encoding.ASCII.GetBytes("zyxwvutsrqzyxwvutsrq");
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1111111109);
    private static string SecretText => Base32.Encode(Secret);

    private static async Task<VaultViewModel> Unlock(Harness h, params EntryData[] entries)
    {
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            foreach (var e in entries) v.AddEntry(e);
            v.Save(3);
        }
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        h.Clock.Now = At;
        return h.Page<VaultViewModel>();
    }

    private static EntryData GitHub(string totp = "") => new()
    {
        Title = "GitHub", Username = "alice", Password = "k#8Vq!2mZr$9Lx@4", Url = "https://github.com", Totp = totp,
    };

    private static EntryData Bank() => new() { Title = "銀行", Username = "1234567", Password = "p@ss-bank-2026", Url = "https://bank.example" };

    [AvaloniaFact]
    public async Task Editor_PastedKey_ShowsCode_AndDetailCountsDown()
    {
        using var h = new Harness();
        var vm = await Unlock(h, GitHub());
        vm.SelectedItem = vm.Items.Single();
        Assert.False(vm.HasTotp);

        vm.EditEntryCommand.Execute(null);
        var editor = vm.Editor!;
        editor.TotpText = "これは違う";
        Assert.StartsWith("⚠", editor.TotpPreview);
        editor.SaveCommand.Execute(null);
        Assert.Contains("ワンタイムパスワードのキー", editor.Error);

        // サイトに出るキー（4 文字ずつ区切り・小文字でもよい）
        editor.TotpText = "gezd gnbv gy3t qojq gezd gnbv gy3t qojq";
        Assert.Contains("081 804", editor.TotpPreview);
        editor.AutoTypeTotp = true;
        h.Window.Height = 1100;
        h.Screenshot("otp-01-editor");
        editor.SaveCommand.Execute(null);
        Assert.Null(vm.Editor);

        var data = vm.Vault.GetEntries().Single().Data;
        Assert.Equal("otpauth://totp/GitHub:alice?secret=" + SecretText + "&issuer=GitHub", data.Totp);
        Assert.True(data.AutoTypeTotp);

        // 詳細: 今のコードと残り秒数。時刻が進めば変わる
        Assert.True(vm.HasTotp);
        Assert.Equal("081 804", vm.TotpCode);
        Assert.Equal(1, vm.TotpRemaining);
        h.Screenshot("otp-02-detail");
        h.Clock.Now = At.AddSeconds(2);
        vm.UpdateTotp();
        Assert.NotEqual("081 804", vm.TotpCode);
        Assert.Equal(29, vm.TotpRemaining);

        // キーを消せば使わない
        vm.EditEntryCommand.Execute(null);
        vm.Editor!.TotpText = "";
        vm.Editor.SaveCommand.Execute(null);
        Assert.Equal("", vm.Vault.GetEntries().Single().Data.Totp);
        Assert.False(vm.HasTotp);
    }

    [AvaloniaFact]
    public async Task Editor_ScansSiteQr_WithCamera()
    {
        using var h = new Harness();
        var vm = await Unlock(h, GitHub());
        vm.SelectedItem = vm.Items.Single();
        vm.EditEntryCommand.Execute(null);
        var editor = vm.Editor!;

        editor.ScanTotpQrCommand.Execute(null);
        var scan = vm.QrScan!;
        Assert.True(scan.CameraSupported);
        h.Screenshot("otp-03-scan");

        await scan.StartCameraCommand.ExecuteAsync(null);
        Assert.True(h.Camera.IsOn);
        // QR の無いコマは読み流し、映像だけ出す
        h.Camera.Send(new CameraFrame(Enumerable.Repeat((byte)200, 320 * 240 * 4).ToArray(), 320, 240));
        await Harness.WaitFor(() => scan.HasPreview);
        h.Screenshot("otp-04-scan-camera");

        // 移行用（複数件）の QR はここでは受け付けず、案内を出す
        h.Camera.Send(TestQr.Frame(Migration([(Secret, "a", "A"), (Secret2, "b", "B")])));
        await Harness.WaitFor(() => scan.Message is not null);
        Assert.Contains("取込/書出", scan.Message);
        Assert.Same(scan, vm.QrScan);

        h.Camera.Send(TestQr.Frame("otpauth://totp/Example:alice?secret=" + SecretText + "&issuer=Example"));
        await Harness.WaitFor(() => vm.QrScan is null);
        Assert.False(h.Camera.IsOn); // 閉じたらカメラを消す
        Assert.Equal("otpauth://totp/Example:alice?secret=" + SecretText + "&issuer=Example", editor.TotpText);
        Assert.Contains("081 804", editor.TotpPreview);
    }

    [AvaloniaFact]
    public async Task Scan_FromImageFile_AndCameraErrors()
    {
        using var h = new Harness();
        var vm = await Unlock(h, GitHub());
        vm.SelectedItem = vm.Items.Single();
        vm.EditEntryCommand.Execute(null);
        vm.Editor!.ScanTotpQrCommand.Execute(null);
        var scan = vm.QrScan!;

        // カメラが使えない（許可されていない）ときは理由を出す
        h.Camera.FailWith = "カメラの使用が許可されていません。";
        await scan.StartCameraCommand.ExecuteAsync(null);
        Assert.False(scan.IsCameraOn);
        Assert.Contains("許可", scan.Error);

        // QR の無い画像
        var blank = Path.Combine(h.Dir, "blank.png");
        TestQr.SavePng("x", blank);
        File.WriteAllBytes(blank, File.ReadAllBytes(blank).Take(40).ToArray()); // 壊れた PNG
        h.Dialogs.Next.Enqueue(blank);
        await scan.OpenImageCommand.ExecuteAsync(null);
        Assert.NotNull(scan.Error);

        // スクリーンショット（PNG）から
        var png = Path.Combine(h.Dir, "qr.png");
        TestQr.SavePng("otpauth://totp/GitHub:alice?secret=" + SecretText, png);
        h.Dialogs.Next.Enqueue(png);
        await scan.OpenImageCommand.ExecuteAsync(null);
        Assert.Null(vm.QrScan);
        Assert.Contains("secret=" + SecretText, vm.Editor!.TotpText);
    }

    [AvaloniaFact]
    public async Task Lock_WhileScanning_TurnsCameraOff()
    {
        using var h = new Harness();
        var vm = await Unlock(h, GitHub());
        vm.ShowImportExportCommand.Execute(null);
        var page = Assert.IsType<ImportExportViewModel>(vm.SubPage);
        page.ScanMigrationCommand.Execute(null);
        await vm.QrScan!.StartCameraCommand.ExecuteAsync(null);
        Assert.True(h.Camera.IsOn);

        h.Main.Lock();
        await Harness.WaitFor(() => !h.Camera.IsOn);
        h.Page<UnlockViewModel>();
    }

    [AvaloniaFact]
    public async Task GoogleAuthenticatorMigration_TwoQrCodes_MatchesAndImports()
    {
        using var h = new Harness();
        var vm = await Unlock(h, GitHub(), Bank(),
            new EntryData { Title = "メール", Username = "bob", Password = "m@il-pass-2026", Totp = new TotpKey(Secret3, issuer: "Mail", account: "bob").ToUri() });
        vm.ShowImportExportCommand.Execute(null);
        var page = Assert.IsType<ImportExportViewModel>(vm.SubPage);

        page.ScanMigrationCommand.Execute(null);
        var scan = vm.QrScan!;
        await scan.StartCameraCommand.ExecuteAsync(null);

        // 1 枚目: GitHub（既存のエントリに合う）と Amazon（新しいエントリ）
        var first = Migration([(Secret, "alice", "GitHub"), (Secret2, "Amazon:carol@example.com", "")], batchSize: 2, batchIndex: 0);
        h.Camera.Send(TestQr.Frame(first));
        await Harness.WaitFor(() => scan.Message?.Contains("1 / 2") == true);
        h.Screenshot("otp-05-migration-scan");

        // 同じ QR をもう一度写しても数えない（写し続けても 1 回だけ扱う）
        scan.Handle(first + "&");
        Assert.Contains("読み取り済み", scan.Message);

        // 2 枚目: 既に PwVault に登録済みのもの
        h.Camera.Send(TestQr.Frame(Migration([(Secret3, "bob", "Mail")], batchSize: 2, batchIndex: 1)));
        await Harness.WaitFor(() => vm.QrScan is null);
        Assert.False(h.Camera.IsOn);

        Assert.Equal(3, page.OtpItems.Count);
        var github = page.OtpItems.Single(i => i.Key.Issuer == "GitHub");
        var amazon = page.OtpItems.Single(i => i.Key.Issuer == "Amazon");
        var mail = page.OtpItems.Single(i => i.Key.Issuer == "Mail");
        Assert.Equal("GitHub（alice）", github.SelectedTarget.Label);
        Assert.True(github.Include);
        Assert.Null(amazon.SelectedTarget.Id); // 新しいエントリを作る
        Assert.Equal("carol@example.com", amazon.Key.Account);
        Assert.False(mail.Include);            // 登録済みなので最初は外す
        Assert.Contains("登録済み", mail.Note);
        h.Window.Height = 1000;
        h.Screenshot("otp-06-migration-review");

        // 同じエントリに 2 つは入れられない
        amazon.SelectedTarget = github.SelectedTarget;
        await page.ImportOtpCommand.ExecuteAsync(null);
        Assert.Contains("1 つのエントリには 1 つだけ", page.OtpStatus);
        amazon.SelectedTarget = amazon.Targets[0];

        var import = page.ImportOtpCommand.ExecuteAsync(null);
        Assert.Contains("新しいエントリを作る: 1 件", h.Main.Confirm!.Message);
        h.Main.ConfirmOkCommand.Execute(null);
        await import;
        Assert.Empty(page.OtpItems);
        Assert.Contains("2 件", page.OtpStatus);

        using var check = Vault.Open(h.VaultPath, Master);
        var entries = check.GetEntries();
        Assert.Equal(4, entries.Count);
        var gh = TotpKey.FromStored(entries.Single(e => e.Data.Title == "GitHub").Data.Totp)!;
        Assert.Equal("081804", gh.Generate(At)); // スマホと同じ番号
        var created = entries.Single(e => e.Data.Title == "Amazon").Data;
        Assert.Equal("carol@example.com", created.Username);
        Assert.True(TotpKey.FromStored(created.Totp)!.SameSecret(new TotpKey(Secret2)));
        Assert.Equal("", entries.Single(e => e.Data.Title == "銀行").Data.Totp);
    }

    [AvaloniaFact]
    public async Task AutoType_TypesCode_AfterPassword()
    {
        using var h = new Harness();
        var game = new TargetWindow(new IntPtr(0x1234), 4321, "ffxivboot.exe", "FINAL FANTASY XIV");
        var entry = GitHub(new TotpKey(Secret).ToUri());
        entry.AutoTypeApps = ["ffxivboot.exe"];
        entry.AutoTypeTotp = true;
        await Unlock(h, entry);
        h.Main.Settings.AutoTypeEnabled = true;
        Assert.Null(h.Main.ApplyAutoTypeSettings());

        h.AutoType.Foreground = game;
        h.AutoType.PressHotKey!();
        Harness.Pump();
        await Assert.IsType<AutoTypePickerViewModel>(h.Main.AutoTypePicker).ChooseCommand.ExecuteAsync(null);
        Assert.Equal(["alice", "<TAB>", "k#8Vq!2mZr$9Lx@4", "<TAB>", "081804"], h.AutoType.Typed);
    }

    /// <summary>Google Authenticator の移行用 QR の中身を組み立てる（Protocol Buffers）。</summary>
    private static string Migration(IEnumerable<(byte[] Secret, string Name, string Issuer)> otps, int batchSize = 1, int batchIndex = 0)
    {
        var payload = new List<byte>();
        foreach (var (secret, name, issuer) in otps)
        {
            var p = new List<byte>();
            Bytes(p, 1, secret);
            Bytes(p, 2, Encoding.UTF8.GetBytes(name));
            Bytes(p, 3, Encoding.UTF8.GetBytes(issuer));
            Varint(p, 4, 1);
            Varint(p, 5, 1);
            Varint(p, 6, 2);
            Bytes(payload, 1, [.. p]);
        }
        Varint(payload, 2, 1);
        Varint(payload, 3, (ulong)batchSize);
        Varint(payload, 4, (ulong)batchIndex);
        Varint(payload, 5, 777);
        return "otpauth-migration://offline?data=" + Uri.EscapeDataString(Convert.ToBase64String([.. payload]));

        static void Raw(List<byte> b, ulong v)
        {
            do
            {
                var x = (byte)(v & 0x7f);
                v >>= 7;
                b.Add(v != 0 ? (byte)(x | 0x80) : x);
            } while (v != 0);
        }
        static void Varint(List<byte> b, int field, ulong v) { Raw(b, (ulong)(field << 3)); Raw(b, v); }
        static void Bytes(List<byte> b, int field, byte[] v) { Raw(b, (ulong)(field << 3 | 2)); Raw(b, (ulong)v.Length); b.AddRange(v); }
    }
}
