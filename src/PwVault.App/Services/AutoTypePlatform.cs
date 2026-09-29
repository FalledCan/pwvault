using System.Runtime.InteropServices;
using System.Text;

namespace PwVault.App.Services;

/// <summary>ショートカットキーを押したときに前面にあったウィンドウ（入力先）。</summary>
public sealed record TargetWindow(IntPtr Handle, int ProcessId, string ProcessName, string Title);

/// <summary>自動タイプを呼び出すショートカットキーの候補。</summary>
public sealed record AutoTypeHotKey(string Id, string Label, uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4;

    public static IReadOnlyList<AutoTypeHotKey> Presets { get; } =
    [
        new("CtrlAltA", "Ctrl + Alt + A", Control | Alt, 'A'),
        new("CtrlAltP", "Ctrl + Alt + P", Control | Alt, 'P'),
        new("CtrlShiftAltA", "Ctrl + Shift + Alt + A", Control | Shift | Alt, 'A'),
        new("CtrlAltInsert", "Ctrl + Alt + Insert", Control | Alt, 0x2D),
    ];

    public static AutoTypeHotKey Find(string? id) => Presets.FirstOrDefault(p => p.Id == id) ?? Presets[0];
}

/// <summary>
/// 自動タイプで使う OS の機能（テストでは偽物に差し替える）。
/// 前面のウィンドウを調べる・前に戻す、文字や Tab を「キーボードで打ったのと同じ形」で送る、ショートカットキーを登録する。
/// </summary>
public interface IAutoTypePlatform
{
    TargetWindow? GetForeground();

    /// <summary>ウィンドウを前面に戻す。戻せたら true。</summary>
    bool Activate(IntPtr handle);

    void TypeText(string text);

    void PressTab();

    /// <summary>ショートカットキーを登録する。ほかのアプリが使っているなど登録できなければ null。解除は Dispose。</summary>
    IDisposable? RegisterHotKey(AutoTypeHotKey key, Action onPressed);
}

public static class AutoTypePlatforms
{
    /// <summary>この OS で使える実装（今は Windows だけ）。</summary>
    public static IAutoTypePlatform? CreateDefault() => OperatingSystem.IsWindows() ? new WindowsAutoTypePlatform() : null;
}

/// <summary>Windows の実装（SendInput・RegisterHotKey など）。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class WindowsAutoTypePlatform : IAutoTypePlatform
{
    public TargetWindow? GetForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;
        GetWindowThreadProcessId(handle, out var pid);
        var title = new StringBuilder(512);
        GetWindowText(handle, title, title.Capacity);
        return new TargetWindow(handle, (int)pid, ProcessImageName((int)pid), title.ToString());
    }

    /// <summary>実行ファイル名。管理者として動いているアプリでも読めるよう、情報を限定した開き方で読む。</summary>
    private static string ProcessImageName(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? Path.GetFileName(buffer.ToString()) : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public bool Activate(IntPtr handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        return SetForegroundWindow(handle);
    }

    public void TypeText(string text)
    {
        // 1 文字ずつ「Unicode の文字」として送る（日本語入力のオン・オフやキー配列の影響を受けない）
        var inputs = new List<Input>();
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue; // 改行などは送らない
            inputs.Add(KeyInput(0, ch, KeyEventUnicode));
            inputs.Add(KeyInput(0, ch, KeyEventUnicode | KeyEventKeyUp));
        }
        Send(inputs);
    }

    public void PressTab() => Send([KeyInput(VkTab, '\0', 0), KeyInput(VkTab, '\0', KeyEventKeyUp)]);

    private static void Send(List<Input> inputs)
    {
        if (inputs.Count == 0) return;
        var sent = SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
            throw new InvalidOperationException("入力を送れませんでした（入力先が管理者として動いている場合など）。");
    }

    private static Input KeyInput(ushort vk, char scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan, Flags = flags } },
    };

    public IDisposable? RegisterHotKey(AutoTypeHotKey key, Action onPressed)
    {
        var registration = new HotKeyThread(key, onPressed);
        return registration.Start() ? registration : null;
    }

    /// <summary>
    /// ショートカットキーを受け取る専用のスレッド。RegisterHotKey にウィンドウを渡さないと、押されたことが
    /// 登録したスレッドのメッセージとして届くので、そのスレッドでメッセージを待ち続ける。
    /// </summary>
    private sealed class HotKeyThread(AutoTypeHotKey key, Action onPressed) : IDisposable
    {
        private const int HotKeyId = 0x5057; // "PW"
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;
        private bool _registered;
        private Thread? _thread;

        public bool Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PwVault hot key" };
            _thread.Start();
            _ready.Wait();
            return _registered;
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _registered = RegisterHotKeyNative(IntPtr.Zero, HotKeyId, key.Modifiers | ModNoRepeat, key.VirtualKey);
            _ready.Set();
            if (!_registered) return;
            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.Message == WmHotKey && (int)msg.WParam == HotKeyId)
                        onPressed();
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HotKeyId);
            }
        }

        public void Dispose()
        {
            if (_registered) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }
    }

    // ------------------------------------------------------------------ Win32

    private const uint InputKeyboard = 1, KeyEventKeyUp = 0x2, KeyEventUnicode = 0x4;
    private const ushort VkTab = 0x09;
    private const uint ModNoRepeat = 0x4000, WmHotKey = 0x0312, WmQuit = 0x0012;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse; // 共用体の大きさを正しくするため
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKeyNative(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
