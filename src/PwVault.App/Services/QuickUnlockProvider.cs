using System.Runtime.InteropServices;
#if WINDOWS
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
#endif

namespace PwVault.App.Services;

/// <summary>
/// 端末の認証機能（Windows Hello など）への窓口。保管庫ごとに取り出せない鍵を作り、challenge に署名してもらう。
/// 署名するたびに OS が顔・指紋・PIN の確認を求める。
/// </summary>
public interface IQuickUnlockProvider
{
    /// <summary>画面に出す名前（例: Windows Hello）。</summary>
    string Name { get; }

    Task<bool> IsAvailableAsync();

    /// <summary>
    /// challenge に署名する。<paramref name="create"/> が true なら保管庫用の鍵を作り直してから署名する。
    /// 利用者が取り消した・鍵が無いなどで署名できなければ null。
    /// </summary>
    Task<byte[]?> SignAsync(Guid vaultId, byte[] challenge, bool create, IntPtr ownerWindow);

    Task DeleteAsync(Guid vaultId);
}

public static class QuickUnlockProviders
{
    /// <summary>この OS で使える実装。無ければ null。</summary>
    public static IQuickUnlockProvider? CreateDefault()
    {
#if WINDOWS
        return new WindowsHelloProvider();
#else
        return null;
#endif
    }
}

#if WINDOWS
/// <summary>Windows Hello（KeyCredentialManager）。鍵は TPM 等で守られ、取り出せない。</summary>
public sealed partial class WindowsHelloProvider : IQuickUnlockProvider
{
    public string Name => "Windows Hello";

    private static string KeyName(Guid vaultId) => "PwVault-" + vaultId.ToString("N");

    public async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
    }

    public async Task<byte[]?> SignAsync(Guid vaultId, byte[] challenge, bool create, IntPtr ownerWindow)
    {
        using var foreground = BringPromptToFront();
        try
        {
            var retrieval = create
                ? await KeyCredentialManager.RequestCreateAsync(KeyName(vaultId), KeyCredentialCreationOption.ReplaceExisting)
                : await KeyCredentialManager.OpenAsync(KeyName(vaultId));
            if (retrieval.Status != KeyCredentialStatus.Success)
                return null;

            var result = await retrieval.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
            if (result.Status != KeyCredentialStatus.Success)
                return null;

            CryptographicBuffer.CopyToByteArray(result.Result, out var signature);
            return signature;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid vaultId)
    {
        try { await KeyCredentialManager.DeleteAsync(KeyName(vaultId)); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException) { }
    }

    /// <summary>
    /// デスクトップアプリから呼ぶと Windows Hello の確認画面がウィンドウの裏に出ることがあるため、
    /// 確認画面（Credential Dialog Xaml Host）が現れたら前面に出す。
    /// </summary>
    private static CancellationTokenSource BringPromptToFront()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var hwnd = FindWindowW("Credential Dialog Xaml Host", null);
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    return;
                }
                try { await Task.Delay(100, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
        return cts;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
#endif
