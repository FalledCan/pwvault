using System.Globalization;

namespace PwVault.Core.Interop;

/// <summary>
/// 平文 CSV への書き出し（FR-11）。出力は暗号化されないため、呼び出し側で警告と再認証を必ず行うこと（SR-12）。
/// ゴミ箱内のエントリは含めない。<see cref="CsvImporter"/> で読み戻せる形式。
/// </summary>
public static class CsvExporter
{
    private static readonly string[] Header = ["title", "username", "password", "url", "notes", "tags", "created_at", "favorite"];

    public static string Export(IEnumerable<VaultEntry> entries) =>
        Csv.Write(
            new[] { (IReadOnlyList<string>)Header }.Concat(
                entries.Where(e => !e.Data.IsTrashed)
                    .OrderBy(e => e.Data.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => (IReadOnlyList<string>)
                    [
                        e.Data.Title,
                        e.Data.Username,
                        e.Data.Password,
                        e.Data.Url,
                        e.Data.Notes,
                        string.Join(';', e.Data.Tags),
                        e.Data.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        e.Data.Favorite ? "true" : "false",
                    ])));
}
