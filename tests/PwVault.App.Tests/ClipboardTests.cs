using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using PwVault.App.Services;

namespace PwVault.App.Tests;

/// <summary>
/// 実際の Windows クリップボードを使うテスト（SR-10）。利用者のクリップボードを書き換えるので通常の実行では走らせない。
/// 実行: dotnet test tests/PwVault.App.Tests -- --explicit only
/// </summary>
public partial class ClipboardTests
{
    [AvaloniaFact(Explicit = true)]
    public void Copy_ExcludesFromHistory_AndClearsOnlyOwnValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 専用");
        var saved = GetText();
        try
        {
            using var service = new ClipboardService(() => IntPtr.Zero);

            service.Copy("pwvault-secret-1", clearAfterSeconds: 60);
            Assert.Equal("pwvault-secret-1", GetText());
            Assert.True(IsFormatAvailable("CanIncludeInClipboardHistory"));
            Assert.True(IsFormatAvailable("CanUploadToCloudClipboard"));
            Assert.True(IsFormatAvailable("ExcludeClipboardContentFromMonitorProcessing"));

            service.ClearIfOwned();
            Assert.Null(GetText());

            // 他のアプリが上書きした値は消さない
            service.Copy("pwvault-secret-2", clearAfterSeconds: 60);
            SetTextAsOtherApp("user copied something else");
            service.ClearIfOwned();
            Assert.Equal("user copied something else", GetText());
        }
        finally
        {
            if (saved is not null) SetTextAsOtherApp(saved);
        }
    }

    private static bool IsFormatAvailable(string name) => IsClipboardFormatAvailable(RegisterClipboardFormatW(name));

    private static unsafe string? GetText()
    {
        var opened = false;
        for (var i = 0; i < 50 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(20);
        if (!opened) throw new InvalidOperationException("クリップボードを開けませんでした。");
        try
        {
            var h = GetClipboardData(13);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { return new string((char*)p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    // ClipboardService を経由しないので、サービスから見ると「他のアプリが書き換えた」状態になる
    private static void SetTextAsOtherApp(string text) => Win32Clipboard.SetText(IntPtr.Zero, text);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(IntPtr h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] private static partial IntPtr GetClipboardData(uint format);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalLock(IntPtr h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(IntPtr h);
}
