using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Banji.Core.IO;

/// <summary>一个工作簿（xlsx/xlsm/csv/tsv/txt）。</summary>
public sealed class WorkbookData
{
    public string SourcePath { get; init; } = "";
    public IReadOnlyList<SheetData> Sheets { get; init; } = Array.Empty<SheetData>();

    public SheetData? Find(string? name)
    {
        if (Sheets.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(name)) return Sheets[0];
        return Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Sheets[0];
    }
}

/// <summary>一个工作表。合并单元格已用左上角值填充。</summary>
public sealed class SheetData
{
    public string Name { get; init; } = "";
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<IReadOnlyList<string>>();

    public int RowCount => Rows.Count;
    public int MaxColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public string Cell(int row, int col)
    {
        if (row < 0 || row >= Rows.Count) return "";
        var r = Rows[row];
        if (col < 0 || col >= r.Count) return "";
        return r[col] ?? "";
    }

    /// <summary>去掉整行为空的行后返回（保留原始行号映射）。</summary>
    public IEnumerable<(int RowIndex, IReadOnlyList<string> Row)> NonEmptyRows()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Any(v => !string.IsNullOrWhiteSpace(v)))
                yield return (i, Rows[i]);
        }
    }
}

/// <summary>
/// 共享表格读取器：xlsx / xlsm（自动处理共享字符串、合并单元格、时间数字格式）
/// 以及 csv / tsv / txt（自动识别分隔符）。不依赖任何第三方库。
/// </summary>
public static class SpreadsheetReader
{
    public static bool IsSupported(string path) => true;

    public static WorkbookData Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xls")
        {
            throw new NotSupportedException("暂不支持旧版 .xls（Excel 97-2003），请在 Excel/WPS 中另存为 .xlsx 后再导入。");
        }
        if (ext is ".csv" or ".tsv" or ".txt" or ".text")
        {
            var sheet = LoadDelimited(path);
            return new WorkbookData { SourcePath = path, Sheets = new[] { sheet } };
        }
        if (ext is ".xlsx" or ".xlsm" or ".xltx" or ".xltm")
        {
            return LoadXlsx(path);
        }

        // 未知扩展名：按内容嗅探
        using (var fs = File.OpenRead(path))
        {
            var sig = new byte[8];
            var n = fs.Read(sig, 0, sig.Length);
            if (n >= 4 && sig[0] == 0x50 && sig[1] == 0x4B) return LoadXlsx(path);
            if (n >= 4 && sig[0] == 0xD0 && sig[1] == 0xCF && sig[2] == 0x11 && sig[3] == 0xE0)
                throw new NotSupportedException("这是旧版 .xls 文件，请在 Excel/WPS 中另存为 .xlsx 后再导入。");
        }
        var delimited = LoadDelimited(path);
        return new WorkbookData { SourcePath = path, Sheets = new[] { delimited } };
    }

    // =========================================================================
    // CSV / TSV
    // =========================================================================

    public static SheetData LoadDelimited(string path)
    {
        var raw = File.ReadAllText(path, DetectEncoding(path));
        var delimiter = DetectDelimiter(raw);
        var rows = ParseDelimited(raw, delimiter);
        return new SheetData { Name = Path.GetFileNameWithoutExtension(path), Rows = rows };
    }

    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        var bom = new byte[4];
        var n = fs.Read(bom, 0, bom.Length);
        if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(false);
        if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        // GB18030 兜底：绝大多数中文 Windows 导出的 CSV 是 GBK/GB18030
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        try
        {
            var bytes = File.ReadAllBytes(path);
            strictUtf8.GetString(bytes);
            return strictUtf8;
        }
        catch
        {
            try { return Encoding.GetEncoding("GB18030"); }
            catch { return Encoding.UTF8; }
        }
    }

    private static char DetectDelimiter(string text)
    {
        var candidates = new[] { ',', '\t', ';', '|' };
        var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(12).ToList();
        if (lines.Count == 0) return ',';
        char best = ',';
        var bestScore = -1;
        foreach (var c in candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, c)).Where(n => n > 0).ToList();
            if (counts.Count == 0) continue;
            var score = counts.Count * 10 + counts.Min();
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == c && !inQuotes) count++;
        }
        return count;
    }

    /// <summary>RFC4180 风格的分隔符解析，支持引号、转义引号、字段内换行。</summary>
    public static List<IReadOnlyList<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '\uFEFF') i = 1;

        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                sb.Append(ch); i++; continue;
            }
            if (ch == '"') { inQuotes = true; i++; continue; }
            if (ch == delimiter) { row.Add(sb.ToString().Trim()); sb.Clear(); i++; continue; }
            if (ch == '\r') { i++; continue; }
            if (ch == '\n')
            {
                row.Add(sb.ToString().Trim());
                rows.Add(NormalizeRow(row));
                row = new List<string>();
                sb.Clear();
                i++;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        if (sb.Length > 0 || row.Count > 0)
        {
            row.Add(sb.ToString().Trim());
            rows.Add(NormalizeRow(row));
        }
        // 去掉尾部连续空行
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static List<string> NormalizeRow(List<string> row)
    {
        while (row.Count > 0 && string.IsNullOrWhiteSpace(row[^1])) row.RemoveAt(row.Count - 1);
        return row;
    }

    // =========================================================================
    // XLSX
    // =========================================================================

    private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static WorkbookData LoadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sharedStrings = ReadSharedStrings(zip);
        var styles = ReadStyles(zip);
        var sheetInfos = ReadSheetInfos(zip);

        var sheets = new List<SheetData>();
        foreach (var info in sheetInfos)
        {
            var rows = ReadSheetRows(zip, info.Path, sharedStrings, styles);
            sheets.Add(new SheetData { Name = info.Name, Rows = rows });
        }
        if (sheets.Count == 0) throw new InvalidDataException("工作簿里没有可读取的工作表。");
        return new WorkbookData { SourcePath = path, Sheets = sheets };
    }

    private const string RelsPath = "xl/_rels/workbook.xml.rels";

    private sealed record SheetInfo(string Name, string Path, int Order);

    private static List<SheetInfo> ReadSheetInfos(ZipArchive zip)
    {
        var result = new List<SheetInfo>();
        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry is null) throw new InvalidDataException("不是有效的 xlsx 文件（缺少 xl/workbook.xml）。");
        using var wbStream = wbEntry.Open();
        var wb = XDocument.Load(wbStream);

        // 关系表 id -> target
        var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relsEntry = zip.GetEntry(RelsPath);
        if (relsEntry is not null)
        {
            using var relsStream = relsEntry.Open();
            var rels = XDocument.Load(relsStream);
            foreach (var rel in rels.Root!.Elements(NsPkgRel + "Relationship"))
            {
                var id = (string?)rel.Attribute("Id");
                var target = (string?)rel.Attribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
                target = target.Replace('\\', '/');
                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                    target = "xl/" + target.TrimStart('/');
                relMap[id] = NormalizeZipPath(target);
            }
        }

        var order = 0;
        foreach (var sheet in wb.Root!.Descendants(NsMain + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? $"Sheet{order + 1}";
            var rid = (string?)sheet.Attribute(NsRel + "id");
            string? target = null;
            if (rid is not null) relMap.TryGetValue(rid, out target);
            target ??= $"xl/worksheets/sheet{order + 1}.xml";
            result.Add(new SheetInfo(name, target, order++));
        }
        return result;
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root!.Elements(NsMain + "si"))
        {
            // 纯文本或富文本（多个 <r><t>），跳过注音 <rPh>
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(NsMain + "t"))
            {
                if (t.Parent?.Name == NsMain + "rPh") continue;
                sb.Append(t.Value);
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    private enum CellFormatKind { General, Date, Time, DateTime }

    private static List<CellFormatKind> ReadStyles(ZipArchive zip)
    {
        var kinds = new List<CellFormatKind>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry is null) return kinds;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var root = doc.Root!;

        var customFormats = new Dictionary<int, string>();
        var numFmts = root.Element(NsMain + "numFmts");
        if (numFmts is not null)
        {
            foreach (var nf in numFmts.Elements(NsMain + "numFmt"))
            {
                var id = (int?)nf.Attribute("numFmtId") ?? -1;
                var code = (string?)nf.Attribute("formatCode") ?? "";
                if (id >= 0) customFormats[id] = code;
            }
        }

        var cellXfs = root.Element(NsMain + "cellXfs");
        if (cellXfs is null) return kinds;
        foreach (var xf in cellXfs.Elements(NsMain + "xf"))
        {
            var numFmtId = (int?)xf.Attribute("numFmtId") ?? 0;
            string? code = customFormats.TryGetValue(numFmtId, out var c) ? c : null;
            kinds.Add(ClassifyFormat(numFmtId, code));
        }
        return kinds;
    }

    private static CellFormatKind ClassifyFormat(int id, string? code)
    {
        // 内置格式
        switch (id)
        {
            case 14: case 15: case 16: case 17: case 22: return CellFormatKind.Date;
            case 18: case 19: case 20: case 21: case 45: case 46: case 47: return CellFormatKind.Time;
        }
        if (string.IsNullOrEmpty(code)) return CellFormatKind.General;

        var cleaned = new StringBuilder();
        var inQuote = false;
        foreach (var ch in code)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch is '[')
            {
                // 跳过 [Red] / [$-409] / [h] 之类；[h] 视为时间
                var end = code.IndexOf(']', code.IndexOf(ch) + 1);
                if (end > 0)
                {
                    var section = code.Substring(code.IndexOf(ch) + 1, end - code.IndexOf(ch) - 1);
                    if (section.Contains('h') || section.Contains('m') || section.Contains('s'))
                        return CellFormatKind.Time;
                    continue;
                }
            }
            if (char.IsLetter(ch)) cleaned.Append(char.ToLowerInvariant(ch));
        }
        var f = cleaned.ToString();
        if (f.Length == 0) return CellFormatKind.General;
        var hasDate = f.Contains('y') || f.Contains('d') || (f.Contains('m') && !f.Contains('h') && !f.Contains('s'));
        var hasTime = f.Contains('h') || f.Contains('s') || (f.Contains('m') && (f.Contains('h') || f.Contains('s')));
        if (hasDate && hasTime) return CellFormatKind.DateTime;
        if (hasTime) return CellFormatKind.Time;
        if (hasDate) return CellFormatKind.Date;
        return CellFormatKind.General;
    }

    private static List<IReadOnlyList<string>> ReadSheetRows(
        ZipArchive zip, string sheetPath, List<string> sharedStrings, List<CellFormatKind> styles)
    {
        var rows = new List<IReadOnlyList<string>>();
        var entry = zip.GetEntry(sheetPath);
        if (entry is null) return rows;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var sheetData = doc.Root!.Element(NsMain + "sheetData");
        if (sheetData is null) return rows;

        var grid = new Dictionary<(int Row, int Col), string>();
        var maxRow = -1;
        var maxCol = -1;
        var lastRow = -1;

        foreach (var rowEl in sheetData.Elements(NsMain + "row"))
        {
            var rowAttr = (int?)rowEl.Attribute("r");
            var rowIndex = rowAttr.HasValue ? rowAttr.Value - 1 : lastRow + 1;
            lastRow = rowIndex;
            var lastCol = -1;
            foreach (var cell in rowEl.Elements(NsMain + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                int colIndex;
                if (!string.IsNullOrEmpty(reference))
                {
                    colIndex = ColumnIndexFromRef(reference);
                }
                else
                {
                    colIndex = lastCol + 1;
                }
                lastCol = colIndex;

                var value = ReadCellValue(cell, sharedStrings, styles);
                if (string.IsNullOrEmpty(value)) continue;
                grid[(rowIndex, colIndex)] = value;
                if (rowIndex > maxRow) maxRow = rowIndex;
                if (colIndex > maxCol) maxCol = colIndex;
            }
        }

        if (maxRow < 0) return rows;

        // 合并单元格：把左上角值填充到整个区域
        var mergeCells = doc.Root!.Element(NsMain + "mergeCells");
        if (mergeCells is not null)
        {
            foreach (var mc in mergeCells.Elements(NsMain + "mergeCell"))
            {
                var reference = (string?)mc.Attribute("ref");
                if (string.IsNullOrEmpty(reference) || !reference.Contains(':')) continue;
                var parts = reference.Split(':');
                var start = ParseRef(parts[0]);
                var end = ParseRef(parts[1]);
                if (start is null || end is null) continue;
                var value = grid.TryGetValue((start.Value.Row, start.Value.Col), out var v) ? v : "";
                if (string.IsNullOrEmpty(value)) continue;
                for (var r = start.Value.Row; r <= end.Value.Row; r++)
                {
                    for (var c = start.Value.Col; c <= end.Value.Col; c++)
                    {
                        if (r == start.Value.Row && c == start.Value.Col) continue;
                        grid[(r, c)] = value;
                        if (r > maxRow) maxRow = r;
                        if (c > maxCol) maxCol = c;
                    }
                }
            }
        }

        for (var r = 0; r <= maxRow; r++)
        {
            var row = new List<string>(maxCol + 1);
            for (var c = 0; c <= maxCol; c++)
                row.Add(grid.TryGetValue((r, c), out var v) ? v : "");
            while (row.Count > 0 && string.IsNullOrWhiteSpace(row[^1])) row.RemoveAt(row.Count - 1);
            rows.Add(row);
        }

        // 去掉尾部空行
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static string ReadCellValue(XElement cell, List<string> sharedStrings, List<CellFormatKind> styles)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        var styleIndex = (int?)cell.Attribute("s") ?? -1;
        var format = styleIndex >= 0 && styleIndex < styles.Count ? styles[styleIndex] : CellFormatKind.General;

        switch (type)
        {
            case "s":
            {
                var raw = cell.Element(NsMain + "v")?.Value;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                    && idx >= 0 && idx < sharedStrings.Count)
                    return sharedStrings[idx].Trim();
                return "";
            }
            case "inlineStr":
                return string.Concat(cell.Descendants(NsMain + "t").Select(t => t.Value)).Trim();
            case "str":
                return (cell.Element(NsMain + "v")?.Value ?? "").Trim();
            case "b":
                return (cell.Element(NsMain + "v")?.Value) == "1" ? "TRUE" : "FALSE";
            case "e":
                return "";
            default:
            {
                var raw = cell.Element(NsMain + "v")?.Value;
                if (string.IsNullOrWhiteSpace(raw)) return "";
                raw = raw.Trim();
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return raw;
                return FormatNumber(number, format);
            }
        }
    }

    private static string FormatNumber(double number, CellFormatKind format)
    {
        switch (format)
        {
            case CellFormatKind.Time:
            {
                var fraction = number - Math.Floor(number);
                var ts = TimeSpan.FromDays(fraction);
                ts = TimeSpan.FromSeconds(Math.Round(ts.TotalSeconds));
                if (ts.TotalSeconds >= 86400) ts = TimeSpan.Zero;
                return ts.Seconds == 0 ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}" : $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
            case CellFormatKind.Date:
                try { return DateTime.FromOADate(number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
                catch { return number.ToString(CultureInfo.InvariantCulture); }
            case CellFormatKind.DateTime:
                try { return DateTime.FromOADate(number).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
                catch { return number.ToString(CultureInfo.InvariantCulture); }
            default:
                if (Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 9e15)
                    return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
                return number.ToString("0.##########", CultureInfo.InvariantCulture);
        }
    }

    private static int ColumnIndexFromRef(string reference)
    {
        var col = 0;
        foreach (var ch in reference)
        {
            if (char.IsLetter(ch)) col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            else break;
        }
        return Math.Max(0, col - 1);
    }

    private static (int Row, int Col)? ParseRef(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var letters = new StringBuilder();
        var digits = new StringBuilder();
        foreach (var ch in reference)
        {
            if (char.IsLetter(ch)) letters.Append(ch);
            else if (char.IsDigit(ch)) digits.Append(ch);
        }
        if (letters.Length == 0 || digits.Length == 0) return null;
        if (!int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)) return null;
        return (row - 1, ColumnIndexFromRef(letters.ToString()));
    }
}
