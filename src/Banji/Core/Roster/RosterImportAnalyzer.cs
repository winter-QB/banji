using System.Text.RegularExpressions;
using Banji.Core.IO;

namespace Banji.Core.Rosters;

/// <summary>花名册表格里一列的角色。</summary>
public enum RosterColumnKind
{
    Unknown = 0,
    StudentId = 1,
    Name = 2,
    Score = 3
}

/// <summary>单个工作表的自动识别结果（导入窗口展示/校验用）。</summary>
public sealed class RosterSheetAnalysis
{
    /// <summary>表头所在行（0-based）；-1 表示无表头。</summary>
    public int HeaderRow { get; init; } = -1;

    /// <summary>表头文字（无表头时为“第1列/第2列…”）。</summary>
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

    public int? IdColumn { get; init; }
    public int? NameColumn { get; init; }
    public int? ScoreColumn { get; init; }

    /// <summary>识别出的数据行数（不含表头、不含空行）。</summary>
    public int DataRowCount { get; init; }

    /// <summary>样例数据行（表头下方最多 12 行）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> SampleRows { get; init; } = Array.Empty<IReadOnlyList<string>>();

    public IReadOnlyList<RosterColumnKind> ColumnKinds { get; init; } = Array.Empty<RosterColumnKind>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasRequiredColumns => IdColumn.HasValue || NameColumn.HasValue;
}

/// <summary>
/// 花名册表格启发式分析器：自动识别表头行、姓名/学号/成绩列，支持无表头自动推断。
/// 同时被 <see cref="RosterService.PreviewImport"/> 与导入窗口使用。
/// </summary>
public static class RosterImportAnalyzer
{
    public const int MaxSampleRows = 12;
    private const int ScanRows = 40;

    // 表头同义词（匹配前会去掉空白/括号内容/常见标点并转小写）
    private static readonly string[] NameWords =
    {
        "姓名", "名字", "学生姓名", "学生名字", "学生", "name", "studentname", "student_name", "fullname"
    };

    private static readonly string[] IdWords =
    {
        "学号", "学籍号", "学生学号", "考号", "编号", "id", "studentid", "student_id", "sid", "学籍", "班内学号"
    };

    private static readonly string[] ScoreWords =
    {
        "成绩", "分数", "得分", "总分", "score", "grade", "point", "分"
    };

    private static readonly Regex ChineseNameRegex = new(@"^[\u4e00-\u9fa5]{2,4}(?:[·•][\u4e00-\u9fa5]{1,6})*$", RegexOptions.Compiled);
    private static readonly Regex PureDigitsRegex = new(@"^\d+$", RegexOptions.Compiled);

    private static readonly Regex StripHeaderRegex = new(@"[\s\(\)（）\[\]【】:：,，.。\-_/\\]+", RegexOptions.Compiled);

    /// <summary>把表头单元格归一化：去空白、去括号内容、去标点、转小写。</summary>
    public static string NormalizeHeader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        // 去掉括号内的说明，如 “姓名（必填）”
        s = Regex.Replace(s, @"[\(（\[【].*?[\)）\]】]", "");
        s = StripHeaderRegex.Replace(s, "");
        return s.ToLowerInvariant();
    }

    /// <summary>按表头文字匹配列角色（不支持时返回 Unknown）。</summary>
    public static RosterColumnKind MatchHeaderCell(string? text)
    {
        var h = NormalizeHeader(text);
        if (h.Length == 0) return RosterColumnKind.Unknown;

        // 顺序很重要：“学号/学生学号”不能在“学生”上先命中了姓名
        if (IdWords.Any(w => h == w)) return RosterColumnKind.StudentId;
        if (NameWords.Any(w => h == w)) return RosterColumnKind.Name;
        if (ScoreWords.Any(w => h == w)) return RosterColumnKind.Score;

        if (IdWords.Any(w => h.Contains(w) && !h.Contains("序号"))) return RosterColumnKind.StudentId;
        if (NameWords.Any(w => h.Contains(w) && !h.Contains("课"))) return RosterColumnKind.Name;
        if (ScoreWords.Any(w => h.Contains(w))) return RosterColumnKind.Score;
        return RosterColumnKind.Unknown;
    }

    /// <summary>分析一个工作表。</summary>
    public static RosterSheetAnalysis Analyze(SheetData sheet) => AnalyzeCore(sheet, null);

    /// <summary>按指定表头行分析（导入窗口里用户手动切换“表头行”时用；-1 表示无表头）。</summary>
    public static RosterSheetAnalysis Analyze(SheetData sheet, int headerRowOverride) => AnalyzeCore(sheet, headerRowOverride);

    private static RosterSheetAnalysis AnalyzeCore(SheetData sheet, int? headerRowOverride)
    {
        var warnings = new List<string>();
        var rows = sheet.Rows;
        if (rows.Count == 0)
        {
            return new RosterSheetAnalysis
            {
                Warnings = new[] { "这个工作表是空的。" }
            };
        }

        int headerRow;
        Dictionary<int, RosterColumnKind> headerKinds;
        if (headerRowOverride is { } forced)
        {
            headerRow = forced >= rows.Count ? -1 : forced;
            headerKinds = new Dictionary<int, RosterColumnKind>();
            if (headerRow >= 0)
            {
                var headerCells = rows[headerRow];
                for (var c = 0; c < headerCells.Count; c++)
                {
                    var kind = MatchHeaderCell(headerCells[c]);
                    if (kind != RosterColumnKind.Unknown) headerKinds[c] = kind;
                }
            }
        }
        else
        {
            headerRow = DetectHeaderRow(rows, out headerKinds);
        }
        var columnCount = Math.Max(rows.Max(r => r.Count), headerRow >= 0 && headerRow < rows.Count ? rows[headerRow].Count : 0);
        if (columnCount == 0) columnCount = 1;

        int? idColumn = null;
        int? nameColumn = null;
        int? scoreColumn = null;

        if (headerRow >= 0)
        {
            for (var c = 0; c < columnCount; c++)
            {
                switch (headerKinds.TryGetValue(c, out var kind) ? kind : RosterColumnKind.Unknown)
                {
                    case RosterColumnKind.StudentId when idColumn is null:
                        idColumn = c;
                        break;
                    case RosterColumnKind.Name when nameColumn is null:
                        nameColumn = c;
                        break;
                    case RosterColumnKind.Score when scoreColumn is null:
                        scoreColumn = c;
                        break;
                }
            }
        }

        var dataStart = headerRow >= 0 ? headerRow + 1 : 0;
        if (idColumn is null && nameColumn is null)
        {
            var inferred = InferColumnsFromData(rows, dataStart, columnCount);
            idColumn = inferred.Id;
            nameColumn = inferred.Name;
            scoreColumn = inferred.Score;
        }
        else if (scoreColumn is null)
        {
            scoreColumn = InferScoreColumn(rows, dataStart, columnCount, idColumn, nameColumn);
        }

        var headers = new List<string>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            var text = headerRow >= 0 ? sheet.Cell(headerRow, c).Trim() : "";
            headers.Add(string.IsNullOrWhiteSpace(text) ? $"第{c + 1}列" : text);
        }

        var sample = new List<IReadOnlyList<string>>();
        var dataRowCount = 0;
        for (var r = dataStart; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            dataRowCount++;
            if (sample.Count < MaxSampleRows)
                sample.Add(Pad(row, columnCount));
        }

        if (!nameColumn.HasValue && !idColumn.HasValue)
        {
            warnings.Add("没有自动识别到姓名或学号列，请在下方手动选择列。");
        }
        else if (!nameColumn.HasValue)
        {
            warnings.Add("只识别到学号列，抽签时将显示学号。");
        }
        else if (!idColumn.HasValue)
        {
            warnings.Add("只识别到姓名列，抽签时将显示姓名。");
        }
        if (dataRowCount == 0) warnings.Add("表头下方没有数据行。");

        var columnKinds = Enumerable.Repeat(RosterColumnKind.Unknown, columnCount).ToArray();
        if (idColumn.HasValue && idColumn < columnCount) columnKinds[idColumn.Value] = RosterColumnKind.StudentId;
        if (nameColumn.HasValue && nameColumn < columnCount) columnKinds[nameColumn.Value] = RosterColumnKind.Name;
        if (scoreColumn.HasValue && scoreColumn < columnCount) columnKinds[scoreColumn.Value] = RosterColumnKind.Score;

        return new RosterSheetAnalysis
        {
            HeaderRow = headerRow,
            Headers = headers,
            IdColumn = idColumn,
            NameColumn = nameColumn,
            ScoreColumn = scoreColumn,
            DataRowCount = dataRowCount,
            SampleRows = sample,
            ColumnKinds = columnKinds,
            Warnings = warnings
        };
    }

    /// <summary>生成“列下拉框”的选项文字。</summary>
    public static IReadOnlyList<string> BuildColumnLabels(SheetData sheet, int headerRow)
    {
        var columnCount = Math.Max(sheet.RowCount == 0 ? 0 : sheet.Rows.Max(r => r.Count), 1);
        var labels = new List<string>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            var header = headerRow >= 0 && headerRow < sheet.RowCount ? sheet.Cell(headerRow, c).Trim() : "";
            labels.Add(string.IsNullOrWhiteSpace(header) ? $"第 {c + 1} 列" : $"第 {c + 1} 列：{header}");
        }
        return labels;
    }

    /// <summary>取出数据区样例行（跳过空行，补齐列宽）。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> BuildPreviewRows(SheetData sheet, int headerRow, int maxRows = MaxSampleRows)
    {
        var columnCount = Math.Max(sheet.RowCount == 0 ? 0 : sheet.Rows.Max(r => r.Count), 1);
        var start = headerRow >= 0 ? headerRow + 1 : 0;
        var result = new List<IReadOnlyList<string>>();
        for (var r = start; r < sheet.RowCount && result.Count < maxRows; r++)
        {
            var row = sheet.Rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            result.Add(Pad(row, columnCount));
        }
        return result;
    }

    /// <summary>取第 n 行的原始文字（用于“表头行”下拉框标签）。</summary>
    public static string DescribeRow(SheetData sheet, int rowIndex, int maxCells = 6)
    {
        if (rowIndex < 0 || rowIndex >= sheet.RowCount) return $"第 {rowIndex + 1} 行";
        var cells = sheet.Rows[rowIndex].Where(v => !string.IsNullOrWhiteSpace(v)).Take(maxCells).ToList();
        var text = string.Join("  |  ", cells);
        if (text.Length > 80) text = text[..80] + "…";
        return string.IsNullOrWhiteSpace(text) ? $"第 {rowIndex + 1} 行（空）" : $"第 {rowIndex + 1} 行：{text}";
    }

    // =========================================================================
    // 实现细节
    // =========================================================================

    private static int DetectHeaderRow(IReadOnlyList<IReadOnlyList<string>> rows, out Dictionary<int, RosterColumnKind> kinds)
    {
        kinds = new Dictionary<int, RosterColumnKind>();
        var limit = Math.Min(rows.Count, 12);
        for (var r = 0; r < limit; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var map = new Dictionary<int, RosterColumnKind>();
            for (var c = 0; c < row.Count; c++)
            {
                var kind = MatchHeaderCell(row[c]);
                if (kind != RosterColumnKind.Unknown) map[c] = kind;
            }
            if (map.Count >= 1)
            {
                kinds = map;
                return r;
            }
        }

        // 未知词表头兜底：第一非空行全是短文本，且后面几行出现“长数字（学号）”列 → 认作表头
        var first = -1;
        for (var r = 0; r < limit; r++)
        {
            if (rows[r].All(string.IsNullOrWhiteSpace)) continue;
            first = r;
            break;
        }
        if (first >= 0)
        {
            var row = rows[first];
            var texts = row.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var allText = texts.Count > 0 && texts.All(v => !PureDigitsRegex.IsMatch(v.Trim()) && v.Trim().Length <= 10);
            var hasLongDigitBelow = false;
            for (var r = first + 1; r < rows.Count && r <= first + 8; r++)
            {
                if (rows[r].Any(v => { var t = v.Trim(); return PureDigitsRegex.IsMatch(t) && t.Length >= 7; }))
                {
                    hasLongDigitBelow = true;
                    break;
                }
            }
            if (allText && hasLongDigitBelow) return first;
        }
        return -1;
    }

    private static (int? Id, int? Name, int? Score) InferColumnsFromData(
        IReadOnlyList<IReadOnlyList<string>> rows,
        int dataStart,
        int columnCount)
    {
        var stats = new List<ColumnStats>(columnCount);
        for (var c = 0; c < columnCount; c++)
            stats.Add(ColumnStats.Build(rows, dataStart, c));

        int? nameColumn = null;
        int? idColumn = null;
        int? scoreColumn = null;

        // 姓名列：中文姓名比例最高
        var nameBest = stats
            .Select((s, i) => (s, i))
            .Where(x => x.s.Count >= 1 && x.s.NameRatio >= 0.6)
            .OrderByDescending(x => x.s.NameRatio)
            .ThenBy(x => x.i)
            .ToList();
        if (nameBest.Count > 0) nameColumn = nameBest[0].i;

        // 学号列：长数字优先；其次“连续唯一短数字”
        var nameIdx = nameColumn;
        var idCandidates = stats
            .Select((s, i) => (s, i))
            .Where(x => x.i != nameIdx && x.s.Count >= 1 && x.s.IdRatio >= 0.6)
            .OrderByDescending(x => x.s.IdRatio)
            .ThenBy(x => x.i)
            .ToList();
        if (idCandidates.Count > 0) idColumn = idCandidates[0].i;

        // 成绩列：0~150 数值（排除已选的学号/姓名列）
        var idIdx = idColumn;
        var scoreCandidates = stats
            .Select((s, i) => (s, i))
            .Where(x => x.i != nameIdx && x.i != idIdx && x.s.Count >= 1 && x.s.ScoreRatio >= 0.6)
            .OrderByDescending(x => x.s.ScoreRatio)
            .ThenBy(x => x.i)
            .ToList();
        if (scoreCandidates.Count > 0) scoreColumn = scoreCandidates[0].i;

        return (idColumn, nameColumn, scoreColumn);
    }

    private static int? InferScoreColumn(
        IReadOnlyList<IReadOnlyList<string>> rows,
        int dataStart,
        int columnCount,
        int? idColumn,
        int? nameColumn)
    {
        var best = -1;
        var bestRatio = 0.6;
        for (var c = 0; c < columnCount; c++)
        {
            if (c == idColumn || c == nameColumn) continue;
            var stats = ColumnStats.Build(rows, dataStart, c);
            if (stats.Count > 0 && stats.ScoreRatio > bestRatio)
            {
                bestRatio = stats.ScoreRatio;
                best = c;
            }
        }
        return best >= 0 ? best : null;
    }

    private static IReadOnlyList<string> Pad(IReadOnlyList<string> row, int columnCount)
    {
        var list = new List<string>(columnCount);
        for (var c = 0; c < columnCount; c++)
            list.Add(c < row.Count ? row[c] ?? "" : "");
        return list;
    }

    private sealed class ColumnStats
    {
        public int Count { get; private set; }
        public double NameRatio { get; private set; }
        public double IdRatio { get; private set; }
        public double ScoreRatio { get; private set; }

        public static ColumnStats Build(IReadOnlyList<IReadOnlyList<string>> rows, int dataStart, int column)
        {
            var values = new List<string>();
            for (var r = dataStart; r < rows.Count && values.Count < 200; r++)
            {
                var row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var v = column < row.Count ? (row[column] ?? "").Trim() : "";
                if (v.Length > 0) values.Add(v);
            }

            var stats = new ColumnStats { Count = values.Count };
            if (values.Count == 0) return stats;

            var unique = values.Distinct(StringComparer.Ordinal).Count() == values.Count;
            var numeric = new List<double>();
            var nameHits = 0;
            var longIdHits = 0;
            var shortIdHits = 0;
            var sequential = true;
            double? previous = null;

            foreach (var v in values)
            {
                if (ChineseNameRegex.IsMatch(v)) nameHits++;

                var isDigits = PureDigitsRegex.IsMatch(v);
                if (isDigits)
                {
                    if (v.Length >= 5) longIdHits++;
                    else if (v.Length >= 2)
                    {
                        shortIdHits++;
                        if (double.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            if (previous.HasValue && Math.Abs(n - previous.Value - 1) > 1e-9) sequential = false;
                            previous = n;
                        }
                    }
                }

                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && d >= 0 && d <= 150 && !(isDigits && v.Length >= 7))
                {
                    numeric.Add(d);
                }
            }

            stats.NameRatio = (double)nameHits / values.Count;
            stats.ScoreRatio = (double)numeric.Count / values.Count;

            var longRatio = (double)longIdHits / values.Count;
            var shortRatio = (double)shortIdHits / values.Count;
            if (longRatio >= 0.6)
            {
                stats.IdRatio = longRatio;
            }
            else if (unique && shortRatio >= 0.6 && values.Count >= 3)
            {
                // 短数字列（如序号 1、2、3…）也常见作学号；必须“整列唯一且基本连续”，否则更像成绩
                stats.IdRatio = sequential ? 0.9 : 0.3;
            }
            else
            {
                stats.IdRatio = Math.Min(longRatio, 0.3);
            }
            return stats;
        }
    }
}
