using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PwVault.Core.Bridge;

namespace PwVault.App.Bridge;

/// <summary>
/// PwVault 本体側の窓口。ブラウザから起動された中継（<see cref="NativeHost"/>）からの要求に、名前付きパイプで答える。
/// ・<see cref="PipeOptions.CurrentUserOnly"/> で、同じ Windows ユーザーのプロセスしか接続できない
/// ・さらに接続元のプロセスが PwVault.exe 自身（中継モード）であることを確かめる
/// ・1 接続 1 要求。要求の処理（保管庫へのアクセス）は UI スレッドで行う
/// </summary>
public sealed partial class BridgeServer : IAsyncDisposable
{
    private readonly Func<BridgeRequest?, Task<BridgeResponse>> _handle;
    private readonly Func<int, bool> _isTrustedClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string PipeName { get; }

    /// <param name="handle">要求への応答を作る（UI スレッドへの切り替えは呼び出し側で行う）。</param>
    /// <param name="isTrustedClient">接続元プロセス ID を受け取り、許可するか返す。省略時は PwVault.exe 自身のみ許可。</param>
    public BridgeServer(Func<BridgeRequest?, Task<BridgeResponse>> handle, string? pipeName = null, Func<int, bool>? isTrustedClient = null)
    {
        _handle = handle;
        _isTrustedClient = isTrustedClient ?? IsSameExecutable;
        PipeName = pipeName ?? DefaultPipeName;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>ユーザーごとに別のパイプ名にする（同じ PC の別ユーザーと衝突しないように）。</summary>
    public static string DefaultPipeName =>
        "PwVault.Bridge." + (OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 待ち受け中のパイプを閉じないと、停止後もそこに接続できてしまい相手が応答待ちで固まる
                await (pipe?.DisposeAsync() ?? ValueTask.CompletedTask);
                return;
            }
            catch (IOException)
            {
                await (pipe?.DisposeAsync() ?? ValueTask.CompletedTask);
                // 同名のパイプが使えないなど。少し待って再試行する
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, ct), ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            if (!_isTrustedClient(GetClientProcessId(pipe)))
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var body = await BridgeMessage.ReadFrameAsync(pipe, BridgeMessage.MaxRequestBytes, timeout.Token);
            if (body is null) return;

            var response = await _handle(BridgeMessage.ParseRequest(body));
            await BridgeMessage.WriteFrameAsync(pipe, BridgeMessage.Serialize(response), timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or EndOfStreamException)
        {
            // 接続が切れた・遅すぎる・不正な長さ。応答せずに閉じる
        }
    }

    /// <summary>接続元のプロセス ID（Windows: GetNamedPipeClientProcessId、macOS: ソケットの LOCAL_PEERPID）。</summary>
    private static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (OperatingSystem.IsWindows())
            return GetNamedPipeClientProcessId(handle, out var pid) ? (int)pid : -1;
        if (OperatingSystem.IsMacOS())
        {
            const int SOL_LOCAL = 0, LOCAL_PEERPID = 2;
            int peer = 0, size = sizeof(int);
            return getsockopt((int)handle, SOL_LOCAL, LOCAL_PEERPID, ref peer, ref size) == 0 ? peer : -1;
        }
        return -1;
    }

    /// <summary>接続元が、いま動いているのと同じ実行ファイル（＝中継モードの PwVault）かどうか。</summary>
    internal static bool IsSameExecutable(int pid)
    {
        if (pid <= 0 || Environment.ProcessPath is not { } self) return false;
        var path = ExecutablePathOf(pid);
        return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(self),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? ExecutablePathOf(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            var buffer = new byte[4096];
            var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
            return length > 0 ? System.Text.Encoding.UTF8.GetString(buffer, 0, length) : null;
        }
        try
        {
            using var client = Process.GetProcessById(pid);
            return client.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(int socket, int level, int optionName, ref int optionValue, ref int optionLength);

    [LibraryImport("/usr/lib/libproc.dylib")]
    private static partial int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
