using PwVault.Core;
using PwVault.Core.Tools;

namespace PwVault.App.Services;

/// <summary>
/// 自動タイプの打ち込み（選択窓からも、アプリが開いたときの自動入力からも使う）。
/// 打つ操作ごとに「前面が入力先のまま（ハンドルとプロセス ID が同じ）」かを確かめ、変わっていたら残りを打たずに止める。
/// </summary>
public static class AutoTyper
{
    /// <param name="activate">先に入力先を前面に戻すか（選択窓から入力するときは true）。</param>
    /// <param name="clock">ワンタイムパスワードを打つときの時刻。</param>
    /// <returns>打てなかったときの理由。打てたら null。</returns>
    public static async Task<string?> TypeAsync(IAutoTypePlatform platform, TargetWindow target, EntryData data, TimeSpan delay, bool activate,
        TimeProvider clock)
    {
        if (AutoTypeMatcher.Sequence(data, clock.GetUtcNow()).Count == 0)
            return "このエントリには、入力するユーザー ID・パスワードがありません。";

        if (activate)
        {
            await Task.Delay(delay);
            if (!platform.Activate(target.Handle))
                return "入力先の画面を前に戻せませんでした。";
            await Task.Delay(delay);
        }

        // ワンタイムパスワードは打つ直前の時刻で作る
        var actions = AutoTypeMatcher.Sequence(data, clock.GetUtcNow());

        try
        {
            foreach (var action in actions)
            {
                if (!IsTarget(platform.GetForeground(), target))
                    return "入力先の画面が切り替わったため、入力を止めました。";
                if (action is AutoTypeAction.Text t) platform.TypeText(t.Value);
                else platform.PressTab();
            }
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        return null;
    }

    public static bool IsTarget(TargetWindow? now, TargetWindow target) =>
        now is not null && now.Handle == target.Handle && now.ProcessId == target.ProcessId;
}
