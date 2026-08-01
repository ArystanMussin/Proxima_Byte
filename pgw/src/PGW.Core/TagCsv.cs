using System.Text;

namespace PGW.Core;

/// <summary>
/// CSV import/export for a source's tag list (§7.1: "Импорт-экспорт тегов в CSV — обязателен... типовой
/// сценарий: 3000 тегов из таблицы"). Deliberately generic rather than one fixed column schema per
/// driver: a tag is already just a flexible key-value dictionary everywhere else in the config model
/// (see <see cref="SourceConfig.Tags"/>), so the CSV header row simply IS the set of keys — a Modbus
/// tag sheet has `area,address` columns, an OPC UA one has `node_id`, a DLMS one has `obis`, and so on;
/// the importer doesn't need to know which.
/// </summary>
public static class TagCsv
{
    /// <summary>Header-driven CSV parse: first row is column names, each following row becomes one tag
    /// dictionary. Handles RFC 4180-style quoted fields (commas/quotes inside `"..."`) since a
    /// round-tripped-through-Excel file will often quote fields, but requires no quoting for the common
    /// case of plain values. A leading UTF-8 BOM (as produced by <see cref="ExportCsv"/> and by Excel's
    /// own CSV export) is stripped if present.</summary>
    public static List<Dictionary<string, object?>> ParseCsv(string csv)
    {
        if (csv.Length > 0 && csv[0] == '﻿') csv = csv[1..];
        var lines = SplitRows(csv);
        if (lines.Count == 0) return new();

        var headers = SplitFields(lines[0]);
        var rows = new List<Dictionary<string, object?>>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue; // tolerate trailing blank lines
            var fields = SplitFields(lines[i]);
            var row = new Dictionary<string, object?>();
            for (var col = 0; col < headers.Count && col < fields.Count; col++)
            {
                if (fields[col].Length > 0) row[headers[col]] = fields[col];
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Union of every key across all rows, in first-seen order with `name` forced first if
    /// present — a row missing a given column simply leaves that cell blank, rather than every row
    /// needing every driver's full field set.</summary>
    public static string ExportCsv(IEnumerable<Dictionary<string, object?>> rows)
    {
        var rowList = rows.ToList();
        var headers = new List<string>();
        var seen = new HashSet<string>();
        void AddHeader(string key) { if (seen.Add(key)) headers.Add(key); }
        if (rowList.Any(r => r.ContainsKey("name"))) AddHeader("name");
        foreach (var row in rowList)
            foreach (var key in row.Keys)
                AddHeader(key);

        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(",", headers)).Append("\r\n");
        foreach (var row in rowList)
        {
            sb.Append(string.Join(",", headers.Select(h => EscapeField(row.TryGetValue(h, out var v) ? v?.ToString() ?? "" : ""))));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string EscapeField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static List<string> SplitRows(string csv)
    {
        // A quoted field can legitimately contain a raw newline, so rows can't be split on '\n' alone
        // without tracking quote state — done here rather than in SplitFields so each line handed to
        // SplitFields is already a complete, well-formed record.
        var rows = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (c == '"') inQuotes = !inQuotes;
            if (c is '\r') continue;
            if (c is '\n' && !inQuotes)
            {
                rows.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) rows.Add(current.ToString());
        return rows;
    }

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
