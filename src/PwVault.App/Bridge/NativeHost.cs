using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PwVault.Core.Bridge;

namespace PwVault.App.Bridge;

/// <summary>
/// ネイティブメッセージングホスト（中継モード）。ブラウザが PwVault.exe をこのモードで起動し、
/// 標準入力で 1 メッセージを渡してくる。それを名前付きパイプで起動中の PwVault 本体に転送し、応答を標準出力に返して終了する。
/// UI は一切作らない。
/// </summary>
public static class NativeHost
{
    public const string HostName = "com.pwvault.native";
    public const string ChromiumExtensionId = "gfjoammhdpkemdcngfdmipagkehaljgl";
    public const string FirefoxExtensionId = "pwvault-autofill@pwvault.local";

    /// <summary>
    /// ブラウザからの起動か。Chrome / Edge は引数に "chrome-extension://ID/" を、
    /// Firefox は「マニフェストのパス」と「拡張の ID」を渡してくる。
    /// </summary>
    public static bool IsNativeMessagingLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(a, FirefoxExtensionId, StringComparison.OrdinalIgnoreCase));

    /// <param name="launchApp">
    /// open の要求で PwVault が起動していなかったときに起動する処理（テストでは差し替える）。省略時は <see cref="LaunchApp"/>。
    /// </param>
    public static async Task<int> RunAsync(Stream stdin, Stream stdout, string pipeName, TimeSpan connectTimeout,
        Action? launchApp = null)
    {
        byte[]? request;
        try
        {
            request = await BridgeMessage.ReadFrameAsync(stdin, BridgeMessage.MaxRequestBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return 1;
        }
        if (request is null) return 0;

        var isOpen = BridgeMessage.ParseRequest(request)?.Type == BridgeRequest.TypeOpen;
        // Windows は、前面にないプロセスが自分のウィンドウを前に出すことを制限している。
        // 中継はブラウザ（前面のプロセス）から起動されているので、ここで PwVault 本体に「前に出てよい」許可を渡す
        if (isOpen && OperatingSystem.IsWindows())
            AllowSetForegroundWindow(ASFW_ANY);

        byte[] response;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(connectTimeout);
            await pipe.ConnectAsync(cts.Token);

            using var io = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await BridgeMessage.WriteFrameAsync(pipe, request, io.Token);
            response = await BridgeMessage.ReadFrameAsync(pipe, BridgeMessage.MaxResponseBytes, io.Token)
                       ?? BridgeMessage.Serialize(BridgeResponse.Fail(BridgeResponse.ErrorNotRunning));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            // PwVault が起動していない、またはブラウザ連携が止まっている。「開く」なら起動する
            if (isOpen)
            {
                try
                {
                    (launchApp ?? LaunchApp)();
                    response = BridgeMessage.Serialize(new BridgeResponse { Ok = true, Unlocked = false, Started = true });
                }
                catch (Exception launchError) when (launchError is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    response = BridgeMessage.Serialize(BridgeResponse.Fail(BridgeResponse.ErrorNotRunning));
                }
            }
            else
            {
                response = BridgeMessage.Serialize(BridgeResponse.Fail(BridgeResponse.ErrorNotRunning));
            }
        }

        await BridgeMessage.WriteFrameAsync(stdout, response);
        return 0;
    }

    /// <summary>
    /// PwVault 本体（UI）を起動する。ブラウザは中継を終わらせるときに子プロセスも一緒に終わらせることがあるので、
    /// Windows はエクスプローラー、macOS は open コマンド経由で、中継とは切り離して起動する。
    /// </summary>
    public static void LaunchApp()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が分かりません。");
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            // …/PwVault.app/Contents/MacOS/PwVault → …/PwVault.app
            var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
            var target = bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : exe;
            Process.Start(new ProcessStartInfo("open", ["-a", target]) { UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
