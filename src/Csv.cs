using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StorageCat;

public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        text = text.TrimStart('\uFEFF');
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQ = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQ = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    public static string Esc(string? s)
    {
        s ??= "";
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static string Row(params object?[] cells) =>
        string.Join(",", cells.Select(c => Esc(c?.ToString())));

    public static int FindCol(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
            if (header[i].Trim().Trim('"').ToLowerInvariant() == name) return i;
        return -1;
    }
}
