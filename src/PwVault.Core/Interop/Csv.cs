using System.Text;

namespace PwVault.Core.Interop;

/// <summary>RFC 4180 形式の CSV の最小実装（引用符・エスケープ・フィールド内改行に対応）。</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1; // BOM

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. row]);
                    row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows.Where(r => r.Any(f => f.Length > 0)).ToList();
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(Escape));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
