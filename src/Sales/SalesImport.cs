using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Palon.Sales;

sealed record ImportResult(List<Deal> Deals, List<string> Warnings);

/// <summary>
/// Imports the user's monthly sheet layout: A Client Name, B date, C
/// ישראל/פרו, D CLUB (tier label), E Source, F deposit amount, G Approved,
/// H FTD Bonus (ignored — recomputed from the rules), I note (usually the
/// affiliate name). Row 1 is a header; rows stop at the first empty A.
/// xlsx is read with a small built-in OpenXML reader (no package added);
/// CSV is supported too. Export writes a UTF-8 BOM CSV Excel opens directly.
/// </summary>
static class SalesImport
{
    /// <summary>Parses sheet rows (header row first). Pure.</summary>
    public static ImportResult ParseRows(IEnumerable<IReadOnlyList<string>> rows, int year, int month)
    {
        var deals = new List<Deal>();
        var warnings = new List<string>();
        int line = 0;
        foreach (var row in rows)
        {
            line++;
            if (line == 1) continue; // header
            string Cell(int i) => i < row.Count ? (row[i] ?? "").Trim() : "";
            var name = Cell(0);
            if (name.Length == 0) break;

            var date = ParseDate(Cell(1), year, month);
            if (date is null) warnings.Add($"Row {line}: unreadable date '{Cell(1)}', used the 1st");
            var region = SalesLabels.ParseRegion(Cell(2));
            if (region is null) warnings.Add($"Row {line}: unknown region '{Cell(2)}', used Pro");
            var source = SalesLabels.ParseSource(Cell(4));
            if (source is null) warnings.Add($"Row {line}: unknown source '{Cell(4)}', used Affiliate");
            if (!TryAmount(Cell(5), out var amount)) warnings.Add($"Row {line}: unreadable amount '{Cell(5)}'");

            var deal = new Deal
            {
                ClientName = name,
                Date = date ?? new DateTime(year, month, 1),
                Region = region ?? DealRegion.Pro,
                Source = source ?? DealSource.Affiliate,
                Amount = amount,
                Approved = ParseBool(Cell(6)),
                CreatedFrom = DealOrigin.Import,
            };
            var club = Cell(3);
            // Keep the sheet's label only when it disagrees with the amount tier.
            if (club.Length > 0 && !string.Equals(club, deal.Tier.ToString(), StringComparison.OrdinalIgnoreCase))
                deal.TierOverride = club;
            var note = Cell(8);
            if (note.Length > 0)
            {
                deal.Note = note;
                deal.Affiliate = note;
            }
            deals.Add(deal);
        }
        return new ImportResult(deals, warnings);
    }

    public static ImportResult FromFile(string path, int year, int month) =>
        ParseRows(
            Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? ReadXlsx(path)
                : ParseCsv(File.ReadAllText(path)),
            year, month);

    static DateTime? ParseDate(string text, int year, int month)
    {
        if (text.Length == 0) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            if (serial >= 1 && serial <= 31 && serial == Math.Floor(serial))
                return new DateTime(year, month, (int)Math.Min(serial, DateTime.DaysInMonth(year, month)));
            if (serial > 59 && serial < 2958465) return DateTime.FromOADate(serial).Date;
        }
        string[] formats = { "d/M/yyyy", "d.M.yyyy", "yyyy-MM-dd", "d/M/yy", "d.M.yy", "d/M", "d.M" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return text.Count(c => c is '/' or '.') == 1 ? new DateTime(year, d.Month, d.Day) : d.Date;
        return null;
    }

    static bool TryAmount(string text, out decimal amount) =>
        decimal.TryParse(text.Replace("$", "").Replace(",", "").Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out amount);

    static bool ParseBool(string text) =>
        text.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "v" or "x" or "✓" or "✔" or "כן" or "אושר";

    // ---- CSV ----

    public static List<IReadOnlyList<string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); break;
                case '﻿' when i == 0: break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    static string CsvCell(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>The month in the import layout (so an export re-imports cleanly).</summary>
    public static string ToCsv(MonthBook book, BonusRules rules)
    {
        var sb = new StringBuilder();
        sb.Append("Client Name,תאריך,ישראל/פרו,CLUB,Source,סכום הפקדה,Approved,FTD Bonus,הערה\r\n");
        foreach (var d in SalesStats.Ordered(book.Deals))
        {
            var fields = new[]
            {
                d.ClientName,
                d.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                SalesLabels.Region(d.Region),
                d.TierLabel,
                SalesLabels.Source(d.Source),
                d.Amount.ToString(CultureInfo.InvariantCulture),
                d.Approved ? "TRUE" : "FALSE",
                rules.FtdBonus(d).ToString(CultureInfo.InvariantCulture),
                d.Affiliate ?? d.Note ?? "",
            };
            sb.Append(string.Join(",", fields.Select(CsvCell))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static bool ExportCsv(MonthBook book, BonusRules rules, string path)
    {
        try
        {
            File.WriteAllText(path, ToCsv(book, rules), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Sales export failed: {ex.Message}");
            return false;
        }
    }

    // ---- minimal xlsx (first worksheet) ----

    static readonly XNamespace Ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<IReadOnlyList<string>> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Load(string name) =>
            zip.GetEntry(name) is { } e ? XDocument.Load(e.Open()) : null;

        var shared = Load("xl/sharedStrings.xml")?.Root?.Elements(Ss + "si")
            .Select(si => string.Concat(si.Descendants(Ss + "t").Select(t => t.Value)))
            .ToList() ?? new List<string>();

        // First sheet in workbook order, resolved through the relationships.
        string sheetPath = "xl/worksheets/sheet1.xml";
        var firstSheet = Load("xl/workbook.xml")?.Root?.Element(Ss + "sheets")?.Elements(Ss + "sheet").FirstOrDefault();
        var relId = (string?)firstSheet?.Attribute(Rel + "id");
        var target = Load("xl/_rels/workbook.xml.rels")?.Root?.Elements(PkgRel + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target")?.Value;
        if (target is not null)
            sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var sheet = Load(sheetPath) ?? throw new InvalidDataException("worksheet not found");
        var rows = new List<IReadOnlyList<string>>();
        foreach (var r in sheet.Descendants(Ss + "row"))
        {
            var cells = new List<string>();
            int next = 0;
            foreach (var c in r.Elements(Ss + "c"))
            {
                int col = ColumnIndex((string?)c.Attribute("r")) ?? next;
                while (cells.Count < col) cells.Add("");
                var type = (string?)c.Attribute("t");
                var raw = c.Element(Ss + "v")?.Value ?? "";
                string value = type switch
                {
                    "s" when int.TryParse(raw, out var i) && i < shared.Count => shared[i],
                    "inlineStr" => string.Concat(c.Descendants(Ss + "t").Select(t => t.Value)),
                    "b" => raw == "1" ? "TRUE" : "FALSE",
                    _ => raw,
                };
                cells.Add(value);
                next = col + 1;
            }
            rows.Add(cells);
        }
        return rows;
    }

    static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        int n = 0;
        foreach (char ch in cellRef)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n > 0 ? n - 1 : null;
    }
}
