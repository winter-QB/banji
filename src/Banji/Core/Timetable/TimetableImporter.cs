using System.Text;
using System.Text.RegularExpressions;
using Banji.Core.Abstractions;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Timetable;

/// <summary>
/// 课表识别器：xlsx / xlsm / csv 课程表 → <see cref="WeekTimetable"/>。
/// <para>支持四种常见布局：</para>
/// <list type="bullet">
///   <item>A. 星期横向表头（周一~周五为列），首列为节次 / 时间；</item>
///   <item>B. 星期纵向表头（周一~周五为行），首行为节次；</item>
///   <item>C. 长表：每行一节课，包含星期列 / 节次列 / 课程列；</item>
///   <item>D. 表头里带时间范围（如 08:00-08:45），按时间识别节次。</item>
/// </list>
/// <para>
/// 识别失败只返回 warnings（不抛异常）；时间缺失时按“每节 45 分钟 + 课间 10 分钟”推算；
/// 节次之间 ≥25 分钟的空档会自动识别为午休/大课间。合并单元格、Excel 时间格式已由
/// <see cref="SpreadsheetReader"/> 提前展开。
/// </para>
/// </summary>
public static class TimetableImporter
{
    private static readonly string[] WeekdaySingleChars = { "一", "二", "三", "四", "五", "六", "日", "天" };

    private static readonly Dictionary<string, DayOfWeek> WeekdayTokens = BuildWeekdayTokens();

    private static readonly string[] BreakKeywords =
    {
        "午休", "午间", "午睡", "午餐", "午饭", "大课间", "课间操", "眼保健操", "课间休息", "课间", "休息",
        "晚餐", "晚饭", "晚休", "放学"
    };

    private static readonly string[] SectionWords = { "上午", "下午", "晚上", "中午", "早上" };

    private static readonly string[] BreakNamePreference =
    {
        "午休", "午间", "午睡", "午餐", "午饭", "大课间", "课间操", "眼保健操", "课间休息", "课间", "休息", "晚餐", "晚饭", "晚休"
    };

    private static readonly TimeSpan DefaultFirstStart = new(8, 0, 0);
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan DefaultGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutoBreakThreshold = TimeSpan.FromMinutes(25);

    // =========================================================================
    // 对外入口
    // =========================================================================

    /// <summary>解析预览（不落盘）。异常一律转换成中文 warning。</summary>
    public static TimetableImportPreview Preview(string filePath, string? sheetName = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("请先选择课表文件。");

            var workbook = SpreadsheetReader.Load(filePath);
            var sheets = workbook.Sheets.Select(s => s.Name).ToList();
            if (sheets.Count == 0)
                return Fail(filePath, sheets, "文件里没有可读取的工作表。");

            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                var sheet = workbook.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));
                if (sheet is null) return Fail(filePath, sheets, $"文件里没有名为“{sheetName}”的工作表。");
                var single = ParseSheet(sheet);
                return BuildPreview(filePath, sheets, sheet.Name, single, $"工作表“{sheet.Name}”");
            }

            ParseResult? best = null;
            var bestSheet = "";
            foreach (var sheet in workbook.Sheets)
            {
                var parsed = ParseSheet(sheet);
                if (best is null || parsed.Score > best.Score)
                {
                    best = parsed;
                    bestSheet = sheet.Name;
                }
            }

            if (best is null) return Fail(filePath, sheets, "文件里没有可读取的工作表。");
            var note = sheets.Count > 1 ? $"文件有 {sheets.Count} 个工作表，已自动选择识别效果最好的“{bestSheet}”。" : "";
            return BuildPreview(filePath, sheets, bestSheet, best, $"工作表“{bestSheet}”", note);
        }
        catch (Exception ex)
        {
            return new TimetableImportPreview
            {
                FilePath = filePath ?? "",
                FormatDescription = "",
                Sheets = Array.Empty<string>(),
                Warnings = new[] { ToFriendlyMessage(ex) }
            };
        }
    }

    /// <summary>真正解析并返回周课表；识别失败抛出中文异常（UI 直接展示给用户）。</summary>
    public static WeekTimetable Import(string filePath, string? sheetName = null)
    {
        var preview = Preview(filePath, sheetName);
        if (preview.Candidate is null)
        {
            var reason = preview.Warnings.Count > 0 ? string.Join("\n", preview.Warnings) : "没有识别出任何课程。";
            throw new InvalidOperationException(reason);
        }
        var timetable = preview.Candidate.Clone();
        timetable.SourceFile = filePath;
        timetable.UpdatedAt = DateTime.Now;
        return timetable;
    }

    private static TimetableImportPreview Fail(string filePath, List<string> sheets, string warning)
        => new()
        {
            FilePath = filePath,
            Sheets = sheets,
            Warnings = new[] { warning }
        };

    private static TimetableImportPreview BuildPreview(
        string filePath, List<string> sheets, string? suggestedSheet, ParseResult result, string sheetPrefix, string? extraNote = null)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraNote)) warnings.Add(extraNote!);
        warnings.AddRange(result.Warnings);
        if (result.Timetable is null && result.Warnings.Count == 0)
            warnings.Add("未能从该工作表识别出课表。");

        var description = result.Timetable is null
            ? result.FormatDescription
            : string.IsNullOrWhiteSpace(result.FormatDescription) ? sheetPrefix : $"{sheetPrefix}：{result.FormatDescription}";

        return new TimetableImportPreview
        {
            FilePath = filePath,
            FormatDescription = description,
            Sheets = sheets,
            SuggestedSheet = suggestedSheet,
            Candidate = result.Timetable,
            Warnings = warnings
        };
    }

    private static string ToFriendlyMessage(Exception ex) => ex switch
    {
        FileNotFoundException => "文件不存在，请重新选择。",
        NotSupportedException => ex.Message,
        InvalidDataException => $"无法读取这个文件：{ex.Message}",
        UnauthorizedAccessException => "没有权限读取该文件，请检查文件是否被占用。",
        IOException => $"读取文件失败：{ex.Message}",
        _ => $"读取/解析课表失败：{ex.Message}"
    };

    // =========================================================================
    // 布局识别
    // =========================================================================

    private sealed class PeriodDraft
    {
        public int Index;
        public string? Name;
        public TimeSpan? Start;
        public TimeSpan? End;
        public string? Section;

        public PeriodDraft Clone() => new() { Index = Index, Name = Name, Start = Start, End = End, Section = Section };
    }

    private sealed class BreakDraft
    {
        public string Name = "课间";
        public TimeSpan? Start;
        public TimeSpan? End;
        public int? AfterPeriodIndex;
    }

    private sealed class EntryDraft
    {
        public DayOfWeek Day;
        public int PeriodIndex;
        public string Course = "";
    }

    private sealed class ParsedGrid
    {
        public readonly List<PeriodDraft> Periods = new();
        public readonly List<BreakDraft> Breaks = new();
        public readonly List<EntryDraft> Entries = new();
        public readonly List<string> Warnings = new();

        public void AddPeriod(PeriodDraft draft)
        {
            var existing = Periods.FirstOrDefault(p => p.Index == draft.Index);
            if (existing is null)
            {
                Periods.Add(draft);
                return;
            }
            existing.Name ??= draft.Name;
            existing.Start ??= draft.Start;
            existing.End ??= draft.End;
            existing.Section ??= draft.Section;
        }

        public void Warn(string message)
        {
            if (Warnings.Count < 20 && !Warnings.Contains(message)) Warnings.Add(message);
        }
    }

    private sealed class ParseResult
    {
        public WeekTimetable? Timetable;
        public string FormatDescription = "";
        public readonly List<string> Warnings = new();
        public int Score;
    }

    private static ParseResult ParseSheet(SheetData sheet)
    {
        var result = new ParseResult();
        try
        {
            if (sheet.Rows.Count < 2)
            {
                result.Warnings.Add("工作表内容太少（少于 2 行），无法识别课表。");
                return result;
            }

            // 顺序很重要：长表 → 星期为列 → 星期为行（含时间表头）
            if (TryParseLongTable(sheet, result)) return result;
            if (TryParseWeekdayColumns(sheet, result)) return result;
            if (TryParseWeekdayRows(sheet, result)) return result;

            result.Warnings.Add("未能识别课表格式：没有找到星期表头（如 周一 / 星期一 / Mon）或课程列。支持“星期为列”“星期为行”“每行一节”“时间表头”等常见布局。");
            return result;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"解析工作表“{sheet.Name}”时出错：{ex.Message}");
            return result;
        }
    }

    // =========================================================================
    // 布局 C：长表（每行一节课）
    // =========================================================================

    private static bool TryParseLongTable(SheetData sheet, ParseResult result)
    {
        var rows = sheet.Rows;
        var limit = Math.Min(rows.Count, 12);
        for (var headerRow = 0; headerRow < limit; headerRow++)
        {
            var headers = rows[headerRow];
            var weekdayCol = -1;
            var courseCol = -1;
            var periodCol = -1;
            var timeCol = -1;
            var startCol = -1;
            var endCol = -1;

            for (var c = 0; c < headers.Count; c++)
            {
                var h = Clean(headers[c]);
                if (h.Length == 0) continue;
                if (weekdayCol < 0 && IsWeekdayHeader(h)) weekdayCol = c;
                else if (courseCol < 0 && IsCourseHeader(h)) courseCol = c;
                else if (periodCol < 0 && IsPeriodHeader(h)) periodCol = c;
                else if (startCol < 0 && IsStartHeader(h)) startCol = c;
                else if (endCol < 0 && IsEndHeader(h)) endCol = c;
                else if (timeCol < 0 && IsTimeHeader(h)) timeCol = c;
            }

            if (courseCol < 0 || (weekdayCol < 0 && periodCol < 0)) continue;

            var grid = new ParsedGrid();
            var dayCounters = new Dictionary<DayOfWeek, int>();
            var dataRows = 0;

            for (var r = headerRow + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                var weekdayText = weekdayCol >= 0 ? Cell(row, weekdayCol) : "";
                DayOfWeek? day = TryParseWeekday(weekdayText, allowSingle: true, out var parsedWeekday) ? parsedWeekday : null;
                if (day is null && weekdayCol < 0)
                {
                    // 没有星期列时，尝试从行首任意单元格识别星期
                    for (var c = 0; c < Math.Min(row.Count, 3); c++)
                    {
                        if (!TryParseWeekday(Cell(row, c), allowSingle: true, out var fromRow)) continue;
                        day = fromRow;
                        break;
                    }
                }

                var periodText = periodCol >= 0 ? Cell(row, periodCol) : "";
                int? index = TryParsePeriodIndex(periodText);
                var course = CourseText(courseCol >= 0 ? Cell(row, courseCol) : "");
                TimeSpan? rangeStart = null;
                TimeSpan? rangeEnd = null;
                if (timeCol >= 0)
                {
                    var range = ParseRange(Cell(row, timeCol));
                    rangeStart = range.Start;
                    rangeEnd = range.End;
                }
                if (rangeStart is null && startCol >= 0) rangeStart = ParseSingleTime(Cell(row, startCol));
                if (rangeEnd is null && endCol >= 0) rangeEnd = ParseSingleTime(Cell(row, endCol));

                if (course.Length > 0 && ContainsBreakKeyword(course))
                {
                    grid.Breaks.Add(new BreakDraft
                    {
                        Name = BreakName(course),
                        Start = rangeStart,
                        End = rangeEnd,
                        AfterPeriodIndex = index
                    });
                    continue;
                }

                if (day is null)
                {
                    if (index is not null || course.Length > 0)
                        grid.Warn($"第 {r + 1} 行没有识别到星期（周一/星期二/Mon 等），已跳过。");
                    continue;
                }

                if (course.Length == 0) continue;

                if (index is null)
                {
                    dayCounters.TryGetValue(day.Value, out var count);
                    index = count + 1;
                    dayCounters[day.Value] = index.Value;
                }
                else
                {
                    dayCounters[day.Value] = Math.Max(dayCounters.TryGetValue(day.Value, out var c) ? c : 0, index.Value);
                }

                if (rangeStart is null && rangeEnd is null) { /* 时间稍后统一推算 */ }

                grid.AddPeriod(new PeriodDraft
                {
                    Index = index.Value,
                    Name = TryParsePeriodIndex(periodText) is not null ? CleanName(periodText) : null,
                    Start = rangeStart,
                    End = rangeEnd,
                    Section = DetectSection(periodText)
                });
                grid.Entries.Add(new EntryDraft { Day = day.Value, PeriodIndex = index.Value, Course = course });
                dataRows++;
            }

            if (dataRows == 0) continue;
            BuildTimetable(grid, result, "长表格式：每行一节课（含星期 / 节次 / 课程列）");
            return result.Timetable is not null;
        }
        return false;
    }

    // =========================================================================
    // 布局 A：星期横向表头（周一~周五为列）
    // =========================================================================

    private static bool TryParseWeekdayColumns(SheetData sheet, ParseResult result)
    {
        var rows = sheet.Rows;
        var headerRow = -1;
        var bestCount = 0;
        Dictionary<int, DayOfWeek> dayCols = new();

        var limit = Math.Min(rows.Count, 12);
        for (var r = 0; r < limit; r++)
        {
            var cols = new Dictionary<int, DayOfWeek>();
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                if (TryParseWeekday(Cell(row, c), allowSingle: true, out var day) && !cols.ContainsKey(c))
                    cols[c] = day;
            }
            var distinct = cols.Values.Distinct().Count();
            if (distinct >= 2 && cols.Count > bestCount)
            {
                bestCount = cols.Count;
                headerRow = r;
                dayCols = cols;
            }
        }

        if (headerRow < 0) return false;

        var minDayCol = dayCols.Keys.Min();
        if (minDayCol == 0)
        {
            // 星期占了第一列：更可能是“星期为行”的转置表，交给下一个解析器。
            return false;
        }

        var grid = new ParsedGrid();
        var lastIndex = 0;
        var anyPeriod = false;
        string? section = null;
        var dataRows = 0;

        for (var r = headerRow + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            // 文件中间重复出现的星期表头行：跳过
            if (row.Count > minDayCol &&
                Enumerable.Range(0, row.Count).Count(c => TryParseWeekday(Cell(row, c), allowSingle: true, out _)) >= 2)
                continue;

            var leading = Enumerable.Range(0, Math.Min(minDayCol, row.Count))
                .Select(c => Clean(Cell(row, c)))
                .Where(s => s.Length > 0)
                .ToList();
            var leadingText = string.Join(" ", leading);

            var (index, start, end) = ParseLeadingLabel(leading, row, minDayCol);

            // 午休 / 大课间行
            if (leadingText.Length > 0 && ContainsBreakKeyword(leadingText))
            {
                grid.Breaks.Add(new BreakDraft
                {
                    Name = BreakName(leadingText),
                    Start = start,
                    End = end,
                    AfterPeriodIndex = index ?? (lastIndex > 0 ? lastIndex : null)
                });
                continue;
            }

            var rowSection = DetectSection(leadingText);
            if (rowSection is not null) section = rowSection;

            var hasCourse = dayCols.Keys.Any(c => c < row.Count && Cell(row, c).Length > 0);
            if (index is null && start is null)
            {
                if (!hasCourse) continue; // 标题行 / 装饰行
                index = anyPeriod ? lastIndex + 1 : 1;
                grid.Warn($"第 {r + 1} 行没有节次/时间，已按顺序推算为第 {index} 节。");
            }
            if (index is null)
            {
                index = anyPeriod ? lastIndex + 1 : 1;
                grid.Warn($"第 {r + 1} 行只有时间没有节次，已按顺序推算为第 {index} 节。");
            }

            grid.AddPeriod(new PeriodDraft
            {
                Index = index.Value,
                Name = null,
                Start = start,
                End = end,
                Section = section
            });
            lastIndex = Math.Max(lastIndex, index.Value);
            anyPeriod = true;

            foreach (var (col, day) in dayCols.OrderBy(kv => kv.Key))
            {
                var cell = CourseText(Cell(row, col));
                if (cell.Length == 0 || IsIgnorableCourse(cell)) continue;
                if (ContainsBreakKeyword(cell))
                {
                    grid.Breaks.Add(new BreakDraft
                    {
                        Name = BreakName(cell),
                        Start = start,
                        End = end,
                        AfterPeriodIndex = index
                    });
                    continue;
                }
                grid.Entries.Add(new EntryDraft { Day = day, PeriodIndex = index.Value, Course = cell });
                dataRows++;
            }
        }

        if (dataRows == 0)
        {
            if (anyPeriod) grid.Warn("识别到了节次和星期表头，但没有读到任何课程内容。");
            return false;
        }
        if (grid.Periods.Count == 0) return false;

        var desc = $"星期横向表头：{dayCols.Values.Distinct().Count()} 天（{string.Join("、", dayCols.Values.Distinct().Select(DayName))}）× {grid.Periods.Count} 节课";
        BuildTimetable(grid, result, desc);
        return result.Timetable is not null;
    }

    /// <summary>从“节次列 / 时间列”中解析节次编号与起止时间（支持分列的开始/结束时间）。</summary>
    private static (int? Index, TimeSpan? Start, TimeSpan? End) ParseLeadingLabel(
        List<string> leading, IReadOnlyList<string> row, int minDayCol)
    {
        int? index = null;
        TimeSpan? start = null;
        TimeSpan? end = null;
        var singles = new List<TimeSpan>();

        foreach (var text in leading)
        {
            if (text.Length == 0) continue;
            if (index is null) index = TryParsePeriodIndex(text);
            var (s, e) = ParseRange(text);
            if (s is not null && e is not null)
            {
                start ??= s;
                end ??= e;
            }
            else
            {
                var single = ParseSingleTime(text);
                if (single is not null) singles.Add(single.Value);
            }
        }

        if (start is null && singles.Count >= 1)
        {
            start = singles[0];
            if (singles.Count >= 2 && end is null) end = singles[1];
        }

        // 时间也可能落在星期列之间的列（例如“上午/下午”占位列）——从整行再扫一次
        if (start is null)
        {
            foreach (var cell in row.Take(Math.Min(row.Count, 4)).Select(Clean))
            {
                var (s, _) = ParseRange(cell);
                if (s is not null) { start = s; break; }
                var single = ParseSingleTime(cell);
                if (single is not null) { start = single; break; }
            }
        }

        return (index, start, end);
    }

    // =========================================================================
    // 布局 B / D：星期纵向表头（周一~周五为行），首行为节次或时间范围
    // =========================================================================

    private static bool TryParseWeekdayRows(SheetData sheet, ParseResult result)
    {
        var rows = sheet.Rows;
        var limit = Math.Min(rows.Count, 12);

        for (var headerRow = 0; headerRow < limit; headerRow++)
        {
            var header = rows[headerRow];

            // 该行不能是“星期表头”（那是布局 A）
            var weekdayInHeader = 0;
            for (var c = 0; c < header.Count; c++)
                if (TryParseWeekday(Cell(header, c), allowSingle: true, out _)) weekdayInHeader++;
            if (weekdayInHeader >= 2) continue;

            var periodCols = new List<(int Col, PeriodDraft Draft)>();
            for (var c = 0; c < header.Count; c++)
            {
                var text = Clean(Cell(header, c));
                if (text.Length == 0) continue;

                var (start, end) = ParseRange(text);
                var index = TryParsePeriodIndex(text);
                if (start is null && index is null) continue;

                periodCols.Add((c, new PeriodDraft
                {
                    Index = index ?? 0,
                    Name = index is not null ? CleanName(text) : null,
                    Start = start,
                    End = end
                }));
            }

            if (periodCols.Count < 2) continue;
            if (periodCols.Select(p => p.Col).Min() == 0) continue; // 首列就是节次，不符合“星期为行”

            var minPeriodCol = periodCols[0].Col;

            // 找出星期所在列
            var rowLabels = new List<(int Row, DayOfWeek Day, string Label)>();
            var labelCol = -1;
            for (var r = headerRow + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                for (var c = 0; c < Math.Min(minPeriodCol, row.Count); c++)
                {
                    if (!TryParseWeekday(Cell(row, c), allowSingle: true, out var day)) continue;
                    rowLabels.Add((r, day, Clean(Cell(row, c))));
                    if (labelCol < 0) labelCol = c;
                    break;
                }
            }

            if (rowLabels.Count == 0) continue;

            var grid = new ParsedGrid();
            var periodsByCol = new Dictionary<int, int>(); // col -> 最终节次序号
            var sequential = 1;
            foreach (var (col, draft) in periodCols.OrderBy(p => p.Col))
            {
                var index = draft.Index > 0 ? draft.Index : sequential;
                sequential = Math.Max(sequential, index) + 1;
                periodsByCol[col] = index;
                grid.AddPeriod(new PeriodDraft
                {
                    Index = index,
                    Name = draft.Name,
                    Start = draft.Start,
                    End = draft.End
                });
            }

            var dataRows = 0;
            var labelRowSet = rowLabels.ToDictionary(x => x.Row, x => x.Day);
            foreach (var (r, day, _) in rowLabels)
            {
                var row = rows[r];
                for (var i = 0; i < periodCols.Count; i++)
                {
                    var col = periodCols[i].Col;
                    var cell = CourseText(Cell(row, col));
                    if (cell.Length == 0 || IsIgnorableCourse(cell)) continue;
                    if (ContainsBreakKeyword(cell))
                    {
                        grid.Breaks.Add(new BreakDraft { Name = BreakName(cell), AfterPeriodIndex = periodsByCol[col] });
                        continue;
                    }
                    grid.Entries.Add(new EntryDraft { Day = day, PeriodIndex = periodsByCol[col], Course = cell });
                    dataRows++;
                }
            }

            // 备注 / 午休行（行首不是星期，但含休息关键字）
            for (var r = headerRow + 1; r < rows.Count; r++)
            {
                if (labelRowSet.ContainsKey(r)) continue;
                var row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var text = string.Join(" ", row.Take(Math.Min(minPeriodCol, row.Count)).Select(Clean).Where(s => s.Length > 0));
                if (text.Length > 0 && ContainsBreakKeyword(text))
                    grid.Breaks.Add(new BreakDraft { Name = BreakName(text) });
            }

            if (dataRows == 0) continue;

            var useTimeHeader = periodCols.Any(p => p.Draft.Start is not null);
            var desc = useTimeHeader
                ? $"时间范围表头：首行为上下课时间（如 08:00-08:45），{rowLabels.Count} 天 × {periodCols.Count} 节"
                : $"星期纵向表头：{rowLabels.Count} 天为行、{periodCols.Count} 节为列";
            BuildTimetable(grid, result, desc);
            return result.Timetable is not null;
        }

        return false;
    }

    // =========================================================================
    // 统一构建：补时间、补午休、重排节次、生成 WeekTimetable
    // =========================================================================

    private static void BuildTimetable(ParsedGrid grid, ParseResult result, string formatDescription)
    {
        if (grid.Periods.Count == 0)
        {
            result.Warnings.AddRange(grid.Warnings);
            result.Warnings.Add("没有识别到任何节次。");
            return;
        }

        var warnings = new List<string>(grid.Warnings);

        // ---- 1. 节次去重 + 排序 ----
        var ordered = grid.Periods
            .GroupBy(p => p.Index)
            .Select(g => g.First())
            .OrderBy(p => p.Start ?? TimeSpan.MaxValue)
            .ThenBy(p => p.Index)
            .ToList();

        // ---- 2. 时间补齐（缺时间时按 45 分钟 + 10 分钟课间推算）----
        var filled = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var period = ordered[i];
            if (period.Start is null)
            {
                period.Start = i == 0
                    ? DefaultFirstStart
                    : (ordered[i - 1].End ?? ordered[i - 1].Start!.Value + DefaultDuration) + DefaultGap;
                filled = true;
            }
            if (period.End is null)
            {
                period.End = period.Start.Value + DefaultDuration;
                filled = true;
            }
            if (period.End <= period.Start)
            {
                period.End = period.Start.Value + DefaultDuration;
                warnings.Add($"第 {i + 1} 节的结束时间不晚于开始时间，已按每节 45 分钟修正。");
            }
        }
        if (filled)
            warnings.Add("部分节次缺少时间，已按“每节 45 分钟、课间 10 分钟”自动推算，可在“课堂时段设置”中修改。");

        // 时间可能被修正后需要重新排序
        ordered = ordered.OrderBy(p => p.Start!.Value).ThenBy(p => p.Index).ToList();

        // ---- 3. 重排节次编号（1..N），并建立旧编号 → 新编号映射 ----
        var indexMap = new Dictionary<int, int>();
        var periods = new List<ClassPeriod>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var draft = ordered[i];
            var newIndex = i + 1;
            indexMap[draft.Index] = newIndex;
            var name = string.IsNullOrWhiteSpace(draft.Name) ? $"第{newIndex}节" : draft.Name!.Trim();
            periods.Add(new ClassPeriod
            {
                Index = newIndex,
                Name = name,
                Start = draft.Start!.Value,
                End = draft.End!.Value
            });
        }

        // ---- 4. 课间 / 午休 ----
        var breaks = new List<BreakSegment>();
        foreach (var draft in grid.Breaks)
        {
            var start = draft.Start;
            var end = draft.End;
            if ((start is null || end is null) && draft.AfterPeriodIndex is not null &&
                indexMap.TryGetValue(draft.AfterPeriodIndex.Value, out var afterNew))
            {
                var after = periods.FirstOrDefault(p => p.Index == afterNew);
                var next = after is null ? null : periods.FirstOrDefault(p => p.Index == afterNew + 1);
                if (after is not null)
                {
                    start ??= after.End;
                    end ??= next?.Start ?? after.End + TimeSpan.FromMinutes(60);
                }
            }
            if (start is not null && end is not null && end > start)
                breaks.Add(new BreakSegment { Name = NormalizeBreakName(draft.Name), Start = start.Value, End = end.Value });
            else if (!string.IsNullOrWhiteSpace(draft.Name))
                warnings.Add($"识别到“{draft.Name}”行，但无法确定其时间，已忽略。");
        }

        // 节次之间的大空档自动识别为午休/大课间
        for (var i = 0; i + 1 < periods.Count; i++)
        {
            var gapStart = periods[i].End;
            var gapEnd = periods[i + 1].Start;
            var gap = gapEnd - gapStart;
            if (gap < AutoBreakThreshold) continue;
            if (breaks.Any(b => b.Start < gapEnd && b.End > gapStart)) continue;

            var name = GuessBreakName(gapStart, gapEnd);
            breaks.Add(new BreakSegment { Name = name, Start = gapStart, End = gapEnd });
            warnings.Add($"第 {i + 1} 节与第 {i + 2} 节之间间隔 {(int)gap.TotalMinutes} 分钟，已自动识别为“{name}”（{FormatTime(gapStart)}-{FormatTime(gapEnd)}），可在“课堂时段设置”中修改。");
        }

        // ---- 5. 课程 ----
        var days = new Dictionary<DayOfWeek, List<TimetableEntry>>();
        foreach (var group in grid.Entries.GroupBy(e => e.Day))
        {
            var list = new List<TimetableEntry>();
            foreach (var entry in group)
            {
                if (!indexMap.TryGetValue(entry.PeriodIndex, out var newIndex)) continue;
                if (string.IsNullOrWhiteSpace(entry.Course)) continue;
                var existing = list.FirstOrDefault(e => e.PeriodIndex == newIndex);
                if (existing is null)
                    list.Add(new TimetableEntry { PeriodIndex = newIndex, Course = entry.Course.Trim() });
                else if (string.IsNullOrWhiteSpace(existing.Course))
                    existing.Course = entry.Course.Trim();
            }
            if (list.Count > 0)
                days[group.Key] = list.OrderBy(e => e.PeriodIndex).ToList();
        }

        if (days.Count == 0)
        {
            result.Warnings.AddRange(warnings);
            result.Warnings.Add("没有识别到任何课程内容（只有节次/时间表）。");
            return;
        }

        // 重叠检查（只提示，不擅自修改）
        for (var i = 0; i + 1 < periods.Count; i++)
        {
            if (periods[i].End > periods[i + 1].Start)
            {
                warnings.Add($"课堂时段存在重叠（{periods[i].Name} {periods[i].TimeText} 与 {periods[i + 1].Name} {periods[i + 1].TimeText}），请到“课堂时段设置”中调整。");
                break;
            }
        }

        var timetable = new WeekTimetable
        {
            SchemaVersion = 1,
            UpdatedAt = DateTime.Now,
            Periods = periods.OrderBy(p => p.Start).ThenBy(p => p.Index).ToList(),
            Breaks = breaks.OrderBy(b => b.Start).ToList(),
            Days = days
        };

        result.Timetable = timetable;
        result.Warnings.AddRange(warnings);
        result.Score = timetable.Days.Values.Sum(d => d.Count) * 2
                       + timetable.Days.Count * 3
                       + timetable.Periods.Count
                       + timetable.Breaks.Count * 2;

        var description = new StringBuilder(formatDescription);
        description.Append($"，共 {timetable.Periods.Count} 节课（{timetable.Periods[0].TimeText} ~ {timetable.Periods[^1].TimeText}）");
        description.Append($"，{timetable.Days.Count} 天 {timetable.Days.Values.Sum(d => d.Count)} 门课");
        if (timetable.Breaks.Count > 0)
            description.Append($"，{timetable.Breaks.Count} 个课间/午休（{string.Join("，", timetable.Breaks.Select(b => $"{b.Name} {b.TimeText}"))}）");
        result.FormatDescription = description.ToString();
    }

    private static TimeSpan? ParseSingleTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = NormalizeTimeText(text);
        if (t.Length == 0) return null;

        var m = Regex.Match(t, @"^(\d{1,2})[:：.．](\d{2})(?:[:：.．](\d{2}))?$");
        if (!m.Success)
            m = Regex.Match(t, @"^(\d{1,2})[时点](\d{1,2})?分?$");
        if (!m.Success) return null;

        var h = int.Parse(m.Groups[1].Value);
        var mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (h is < 0 or > 23 || mi is < 0 or > 59 || s is < 0 or > 59) return null;
        return new TimeSpan(h, mi, s);
    }

    private static (TimeSpan? Start, TimeSpan? End) ParseRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var t = NormalizeTimeText(text);

        var m = Regex.Match(t,
            @"(\d{1,2})[:：.．](\d{2})(?:[:：.．](\d{2}))?\s*[-—–~～至到]\s*(\d{1,2})[:：.．](\d{2})(?:[:：.．](\d{2}))?");
        if (!m.Success) return (null, null);

        var h1 = int.Parse(m.Groups[1].Value);
        var m1 = int.Parse(m.Groups[2].Value);
        var s1 = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        var h2 = int.Parse(m.Groups[4].Value);
        var m2 = int.Parse(m.Groups[5].Value);
        var s2 = m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0;
        if (h1 is < 0 or > 23 || h2 is < 0 or > 23 || m1 is < 0 or > 59 || m2 is < 0 or > 59) return (null, null);

        var start = new TimeSpan(h1, m1, s1);
        var end = new TimeSpan(h2, m2, s2);
        if (end <= start) end += TimeSpan.FromHours(12); // 12 小时制写法兜底
        return end > start ? (start, end) : (null, null);
    }

    private static string NormalizeTimeText(string text)
    {
        var t = text.Trim();
        t = t.Replace('：', ':').Replace('．', '.').Replace('。', '.');
        t = t.Replace('－', '-').Replace('—', '-').Replace('–', '-').Replace('~', '-').Replace('～', '-');
        t = t.Replace("至", "-").Replace("到", "-").Replace("始", "-");
        t = Regex.Replace(t, @"\s+", "");
        return t;
    }

    // =========================================================================
    // 文本识别辅助
    // =========================================================================

    private static Dictionary<string, DayOfWeek> BuildWeekdayTokens()
    {
        var dict = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase);
        var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
        var days = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        };
        for (var i = 0; i < names.Length; i++)
        {
            dict["周" + names[i]] = days[i];
            dict["星期" + names[i]] = days[i];
            dict["礼拜" + names[i]] = days[i];
            dict["週" + names[i]] = days[i];
        }
        dict["星期天"] = DayOfWeek.Sunday;
        dict["周天"] = DayOfWeek.Sunday;
        dict["礼拜天"] = DayOfWeek.Sunday;
        dict["週天"] = DayOfWeek.Sunday;

        dict["monday"] = DayOfWeek.Monday;
        dict["mon"] = DayOfWeek.Monday;
        dict["mo"] = DayOfWeek.Monday;
        dict["tuesday"] = DayOfWeek.Tuesday;
        dict["tue"] = DayOfWeek.Tuesday;
        dict["tues"] = DayOfWeek.Tuesday;
        dict["tu"] = DayOfWeek.Tuesday;
        dict["wednesday"] = DayOfWeek.Wednesday;
        dict["wed"] = DayOfWeek.Wednesday;
        dict["we"] = DayOfWeek.Wednesday;
        dict["thursday"] = DayOfWeek.Thursday;
        dict["thu"] = DayOfWeek.Thursday;
        dict["thur"] = DayOfWeek.Thursday;
        dict["thurs"] = DayOfWeek.Thursday;
        dict["th"] = DayOfWeek.Thursday;
        dict["friday"] = DayOfWeek.Friday;
        dict["fri"] = DayOfWeek.Friday;
        dict["fr"] = DayOfWeek.Friday;
        dict["saturday"] = DayOfWeek.Saturday;
        dict["sat"] = DayOfWeek.Saturday;
        dict["sa"] = DayOfWeek.Saturday;
        dict["sunday"] = DayOfWeek.Sunday;
        dict["sun"] = DayOfWeek.Sunday;
        dict["su"] = DayOfWeek.Sunday;
        return dict;
    }

    private static bool TryParseWeekday(string? text, bool allowSingle, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // 去掉括号注释："星期一(Mon)" / "周一（上午）"
        t = Regex.Replace(t, @"[（(].*?[）)]", "").Trim();
        t = t.Replace(" ", "").Replace("\u3000", "");
        if (t.Length == 0) return false;

        if (WeekdayTokens.TryGetValue(t, out day)) return true;

        if (allowSingle && t.Length == 1 && Array.IndexOf(WeekdaySingleChars, t) >= 0)
        {
            day = t switch
            {
                "一" => DayOfWeek.Monday,
                "二" => DayOfWeek.Tuesday,
                "三" => DayOfWeek.Wednesday,
                "四" => DayOfWeek.Thursday,
                "五" => DayOfWeek.Friday,
                "六" => DayOfWeek.Saturday,
                _ => DayOfWeek.Sunday
            };
            return true;
        }
        return false;
    }

    /// <summary>解析“第 3 节 / 三节 / 3 / 第三节”里的节次编号。</summary>
    private static int? TryParsePeriodIndex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Clean(text).Replace(" ", "");
        t = Regex.Replace(t, @"[（(].*?[）)]", "");
        if (t.Length == 0) return null;

        var m = Regex.Match(t, @"第\s*([0-9]{1,2})\s*节");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n1) && n1 is > 0 and <= 30) return n1;

        m = Regex.Match(t, @"^([0-9]{1,2})(?:节|课)?$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n2) && n2 is > 0 and <= 30) return n2;

        m = Regex.Match(t, @"^([一二三四五六七八九十]{1,3})(?:节|课)?$");
        if (m.Success && TryParseChineseNumber(m.Groups[1].Value, out var n3) && n3 is > 0 and <= 30) return n3;

        m = Regex.Match(t, @"第\s*([一二三四五六七八九十]{1,3})\s*节");
        if (m.Success && TryParseChineseNumber(m.Groups[1].Value, out var n4) && n4 is > 0 and <= 30) return n4;

        return null;
    }

    private static bool TryParseChineseNumber(string text, out int value)
    {
        value = 0;
        var map = new Dictionary<char, int>
        {
            ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5,
            ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10
        };
        if (text.Length == 0) return false;
        if (text.Length == 1 && map.TryGetValue(text[0], out value)) return true;
        if (text.Length == 2 && text[0] == '十' && map.TryGetValue(text[1], out var unit)) { value = 10 + unit; return true; }
        if (text.Length == 2 && text[1] == '十' && map.TryGetValue(text[0], out var tens)) { value = tens * 10; return true; }
        if (text.Length == 3 && text[1] == '十' &&
            map.TryGetValue(text[0], out var t) && map.TryGetValue(text[2], out var u)) { value = t * 10 + u; return true; }
        return false;
    }

    private static bool IsWeekdayHeader(string header)
    {
        var h = header.Replace(" ", "");
        if (h is "星期" or "星期几" or "周几" or "周" or "日期" or "上课日") return true;
        return h.Length <= 4 && (h.StartsWith("星期") || h.StartsWith("礼拜")) && TryParseWeekday(h, allowSingle: false, out _) == false && h is not "星期天" and not "星期日";
    }

    private static bool IsCourseHeader(string header)
        => header.Contains("课程") || header.Contains("科目") || header.Contains("学科") || header.Contains("教学内容");

    private static bool IsPeriodHeader(string header)
        => header is "节" or "节次" || header.Contains("节次") || header.Contains("课节") || header.Contains("课时") || header.Contains("第几节");

    private static bool IsStartHeader(string header) => header.Contains("开始") || header.Contains("起");

    private static bool IsEndHeader(string header) => header.Contains("结束") || header.Contains("止");

    private static bool IsTimeHeader(string header)
        => (header.Contains("时间") || header.Contains("时段")) && !header.Contains("开始") && !header.Contains("结束");

    private static bool ContainsBreakKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Replace(" ", "");
        return BreakKeywords.Any(k => t.Contains(k));
    }

    private static string BreakName(string text)
    {
        var t = text.Replace(" ", "");
        foreach (var keyword in BreakNamePreference)
            if (t.Contains(keyword)) return keyword;
        return "课间";
    }

    private static string NormalizeBreakName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "课间";
        var n = name.Trim();
        return n.Length > 12 ? "课间" : n;
    }

    private static string GuessBreakName(TimeSpan gapStart, TimeSpan gapEnd)
    {
        var startHour = (int)gapStart.TotalHours;
        var endHour = (int)gapEnd.TotalHours;
        if (startHour <= 13 && endHour >= 11) return "午休";
        return "大课间";
    }

    private static string? DetectSection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var word in SectionWords)
            if (text.Contains(word)) return word;
        return null;
    }

    private static bool IsIgnorableCourse(string text)
    {
        var t = text.Replace(" ", "");
        if (t.Length == 0) return true;
        if (t is "-" or "—" or "–" or "／" or "/" or "…" or "···" or "...") return true;
        foreach (var word in SectionWords) if (t == word) return true;
        return false;
    }

    /// <summary>课程单元格文本：多行时取第一行（通常是课程名，后面常是老师/教室）。</summary>
    private static string CourseText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Regex.Replace(s.Trim(), @"\s+", " "))
            .Where(s => s.Length > 0)
            .ToList();
        if (lines.Count == 0) return "";
        var first = lines[0];
        return first.Length > 40 ? first[..40] + "…" : first;
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static string CleanName(string text)
    {
        var t = Clean(text);
        return t.Length > 20 ? t[..20] : t;
    }

    private static string Cell(IReadOnlyList<string> row, int col)
        => col >= 0 && col < row.Count ? (row[col] ?? "") : "";

    private static string FormatTime(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
