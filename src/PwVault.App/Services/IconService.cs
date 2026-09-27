using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PwVault.Core;
using PwVault.Core.Bridge;
using PwVault.Core.Icons;

namespace PwVault.App.Services;

/// <summary>
/// アンロック中の保管庫に対応するアイコンの管理。暗号化キャッシュの読み書き、画像の読み込み、
/// サイトからの取得（設定でオンのとき）を行う。キャッシュと保管庫には UI スレッドからだけ触る。
/// </summary>
public sealed class IconService : IDisposable
{
    private const int MaxParallelFetches = 4;

    private readonly Vault _vault;
    private readonly IconCache _cache;
    private readonly string _path;
    private readonly FaviconFetcher _fetcher;
    private readonly Dictionary<string, Bitmap?> _bitmaps = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _inFlight = [];
    private DispatcherTimer? _saveTimer;

    public IconService(Vault vault, FaviconFetcher? fetcher = null)
    {
        _vault = vault;
        _path = IconCache.PathFor(vault.FilePath);
        _cache = IconCache.Load(vault, _path);
        _fetcher = fetcher ?? new FaviconFetcher();
    }

    /// <summary>あるホストのアイコンが新しく手に入った・変わった。</summary>
    public event Action<string>? IconChanged;

    /// <summary>エントリの URL に対応するアイコン。無ければ null（頭文字の丸を表示する）。</summary>
    public Bitmap? GetIcon(string url)
    {
        if (UrlMatcher.HostKey(url) is not { } host) return null;
        if (_bitmaps.TryGetValue(host, out var cached)) return cached;
        var bitmap = _cache.Get(host)?.Data is { } data ? TryDecode(data) : null;
        _bitmaps[host] = bitmap;
        return bitmap;
    }

    /// <summary>ブラウザ拡張から受け取ったアイコンを保存する（画像として読めるものだけ）。</summary>
    public void StoreFromBrowser(string host, byte[] data)
    {
        if (_cts.IsCancellationRequested || TryDecode(data) is not { } bitmap) return;
        _cache.SetFromBrowser(host, data, DateTimeOffset.UtcNow);
        Replace(host, bitmap);
        ScheduleSave();
    }

    /// <summary>まだアイコンの無いサイトについて、各サイトから取得を始める（設定でオンのときだけ呼ぶ）。</summary>
    public void FetchMissing(IEnumerable<VaultEntry> entries)
    {
        var now = DateTimeOffset.UtcNow;
        var targets = entries
            .Where(e => !e.Data.IsTrashed)
            .Select(e => (Host: UrlMatcher.HostKey(e.Data.Url), e.Data.Url))
            .Where(t => t.Host is not null && _cache.NeedsSiteFetch(t.Host, now) && !_inFlight.Contains(t.Host))
            .DistinctBy(t => t.Host)
            .ToList();
        if (targets.Count == 0) return;

        foreach (var t in targets) _inFlight.Add(t.Host!);
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(MaxParallelFetches);
            await Task.WhenAll(targets.Select(async t =>
            {
                try { await gate.WaitAsync(ct); }
                catch (OperationCanceledException) { return; }
                try
                {
                    var uri = new Uri(t.Url.Contains("://") ? t.Url : "https://" + t.Url);
                    var data = await _fetcher.FetchAsync(uri, d => TryDecode(d) is { } b && ReleaseBitmap(b), ct);
                    await Dispatcher.UIThread.InvokeAsync(() => OnFetched(t.Host!, data));
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            }));
        }, ct);
    }

    private static bool ReleaseBitmap(Bitmap bitmap)
    {
        bitmap.Dispose();
        return true;
    }

    private void OnFetched(string host, byte[]? data)
    {
        _inFlight.Remove(host);
        if (_cts.IsCancellationRequested) return;
        if (!_cache.SetFromSite(host, data, DateTimeOffset.UtcNow)) return;
        if (data is not null && TryDecode(data) is { } bitmap)
            Replace(host, bitmap);
        ScheduleSave();
    }

    /// <summary>削除済みエントリのアイコンをキャッシュから消す。</summary>
    public void Prune(IEnumerable<VaultEntry> entries)
    {
        var hosts = entries.Select(e => UrlMatcher.HostKey(e.Data.Url)).OfType<string>().ToHashSet();
        if (_cache.RemoveAllExcept(hosts) == 0) return;
        foreach (var host in _bitmaps.Keys.Where(h => !hosts.Contains(h)).ToList())
            _bitmaps.Remove(host);
        ScheduleSave();
    }

    private void Replace(string host, Bitmap bitmap)
    {
        // 古い Bitmap は画面から外れた後で捨てられるよう、ここでは Dispose しない
        _bitmaps[host] = bitmap;
        IconChanged?.Invoke(host);
    }

    /// <summary>連続して届くことが多いので、少しまとめてから保存する。</summary>
    private void ScheduleSave()
    {
        if (_saveTimer is not null) return;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer?.Stop();
        _saveTimer = null;
        if (_vault.IsLocked) return;
        try { _cache.Save(_vault, _path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* アイコンは取り直せる */ }
    }

    private static Bitmap? TryDecode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width is > 0 and <= 1024 && bitmap.PixelSize.Height is > 0 and <= 1024)
                return bitmap;
            bitmap.Dispose();
            return null;
        }
        catch (Exception)
        {
            // 壊れた画像・対応していない形式（デコーダが投げる例外は種類が一定しない）
            return null;
        }
    }

    /// <summary>ロック前に呼ぶ。未保存があれば保存し、取得を止める。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        if (_saveTimer is not null) SaveNow();
        _bitmaps.Clear();
        // 取得中のタスクがまだトークンを見ているので、CancellationTokenSource は Dispose しない
    }
}
