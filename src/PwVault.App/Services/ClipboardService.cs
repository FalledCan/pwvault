using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace PwVault.App.Services;

/// <summary>
/// クリップボードへのコピーと自動クリア（FR-08, SR-10）。
/// Windows では Win32 API を直接使い、クリップボード履歴・クラウド同期・監視ツールから除外する形式を付ける。
/// クリアは、アプリ自身がコピーした値がまだ残っている場合（シーケンス番号が変わっていない場合）に限る。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly Func<IntPtr> _ownerWindow;
    private DispatcherTimer? _timer;
    private uint _ourSequence;

    public ClipboardService(Func<IntPtr> ownerWindow) => _ownerWindow = ownerWindow;

    /// <summary>クリアまでの残り秒数が変わったとき（0 でクリア済み／対象なし）。</summary>
    public event Action<int>? CountdownChanged;

    public void Copy(string text, int clearAfterSeconds)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("このバージョンのクリップボード機能は Windows 専用です。");

        Win32Clipboard.SetText(_ownerWindow(), text);
        _ourSequence = Win32Clipboard.GetSequenceNumber();
        StartCountdown(clearAfterSeconds);
    }

    /// <summary>アプリがコピーした値がまだ残っていれば消す（ロック時・終了時にも呼ぶ）。</summary>
    public void ClearIfOwned()
    {
        StopCountdown();
        if (_ourSequence == 0 || !OperatingSystem.IsWindows())
            return;
        if (Win32Clipboard.GetSequenceNumber() == _ourSequence)
            Win32Clipboard.Clear(_ownerWindow());
        _ourSequence = 0;
    }

    private void StartCountdown(int seconds)
    {
        StopCountdown();
        var remaining = seconds;
        CountdownChanged?.Invoke(remaining);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            remaining--;
            // 他のアプリが上書きしたら、こちらの値ではないのでカウントダウンをやめる
            if (remaining <= 0 || (OperatingSystem.IsWindows() && Win32Clipboard.GetSequenceNumber() != _ourSequence))
                ClearIfOwned();
            else
                CountdownChanged?.Invoke(remaining);
        };
        _timer.Start();
    }

    private void StopCountdown()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
        CountdownChanged?.Invoke(0);
    }

    public void Dispose() => ClearIfOwned();
}

internal static unsafe partial class Win32Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GHND = 0x0042; // GMEM_MOVEABLE | GMEM_ZEROINIT

    public static uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public static void SetText(IntPtr owner, string text)
    {
        Open(owner);
        try
        {
            if (!EmptyClipboard())
                throw new InvalidOperationException("クリップボードを初期化できませんでした。");

            SetData(CF_UNICODETEXT, (text.Length + 1) * sizeof(char), p =>
                text.AsSpan().CopyTo(new Span<char>((void*)p, text.Length)));

            // Windows のクリップボード履歴（Win+V）とクラウド同期、監視ツールから除外する
            SetDword(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), 0);
            SetDword(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), 0);
            SetDword(RegisterClipboardFormatW("CanUploadToCloudClipboard"), 0);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void Clear(IntPtr owner)
    {
        Open(owner);
        try { EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    private static void Open(IntPtr owner)
    {
        // 他のプロセスが一時的に開いていることがあるので少し待って再試行する
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(owner)) return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("クリップボードを開けませんでした。");
    }

    private static void SetDword(uint format, uint value) =>
        SetData(format, sizeof(uint), p => *(uint*)p = value);

    private static void SetData(uint format, int size, Action<IntPtr> write)
    {
        var handle = GlobalAlloc(GHND, (nuint)size);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException();

        var ptr = GlobalLock(handle);
        try { write(ptr); }
        finally { GlobalUnlock(handle); }

        // 成功すると所有権は OS に移る。失敗したときだけ自分で解放する
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("クリップボードに書き込めませんでした。");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string lpszFormat);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}
