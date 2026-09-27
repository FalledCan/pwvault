using System.IO.Pipes;
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

    public static async Task<int> RunAsync(Stream stdin, Stream stdout, string pipeName, TimeSpan connectTimeout)
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
            // PwVault が起動していない、またはブラウザ連携が止まっている
            response = BridgeMessage.Serialize(BridgeResponse.Fail(BridgeResponse.ErrorNotRunning));
        }

        await BridgeMessage.WriteFrameAsync(stdout, response);
        return 0;
    }
}
