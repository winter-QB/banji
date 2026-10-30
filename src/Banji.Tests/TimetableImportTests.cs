using System.IO.Compression;
using System.Text;
using Banji.Core.Timetable;

namespace Banji.Tests;

// ============================================================================
// 测试用临时 xlsx / csv 生成器（不依赖任何第三方库）
// ============================================================================

internal sealed class XlsxSheet
{
    public string Name = "Sheet1";
    public List<string[]> Rows = new();
    public List<string> Merges = new();

    public XlsxSheet Row(params string[] cells)
    {
        Rows.Add(cells);
        return this;
    }

    /// <summary>合并单元格，如 "A2:A4"。</summary>
    public XlsxSheet Merge(string range)
    {
        Merges.Add(range);
        return this;
    }
}

internal static class TestFileBuilder
{
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "banji-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Escape(string? text)
        => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string ColumnName(int index)
    {
        var name = "";
        var n = index + 1;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            name = (char)('A' + rem) + name;
            n = (n - 1) / 26;
        }
        return name;
    }

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<cellXfs count=\"2\"><xf numFmtId=\"0\" xfId=\"0\"/><xf numFmtId=\"18\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
        "</styleSheet>";

    /// <summary>写出一个最小 xlsx。单元格以 "TIME:08:00" 表示 Excel 时间格式（numFmtId=18 的数字单元格）。</summary>
    public static string WriteXlsx(string fileName, params XlsxSheet[] sheets)
    {
        var dir = TempDir();
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>");

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var i = 0; i < sheets.Length; i++)
            sb.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        WriteEntry(zip, "xl/workbook.xml", sb.ToString());

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        rels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 0; i < sheets.Length; i++)
            rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        rels.Append("</Relationships>");
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", rels.ToString());

        WriteEntry(zip, "xl/styles.xml", StylesXml);

        for (var i = 0; i < sheets.Length; i++)
        {
            var sheet = sheets[i];
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                xml.Append($"<row r=\"{r + 1}\">");
                var row = sheet.Rows[r];
                for (var c = 0; c < row.Length; c++)
                {
                    var cell = row[c];
                    if (string.IsNullOrEmpty(cell)) continue;
                    var rRef = ColumnName(c) + (r + 1);
                    if (cell.StartsWith("TIME:", StringComparison.OrdinalIgnoreCase))
                    {
                        var ts = TimeSpan.ParseExact(cell[5..], @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
                        var serial = ts.TotalSeconds / 86400d;
                        xml.Append($"<c r=\"{rRef}\" s=\"1\"><v>{serial.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        xml.Append($"<c r=\"{rRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cell)}</t></is></c>");
                    }
                }
                xml.Append("</row>");
            }
            xml.Append("</sheetData>");
            if (sheet.Merges.Count > 0)
            {
                xml.Append($"<mergeCells count=\"{sheet.Merges.Count}\">");
                foreach (var merge in sheet.Merges) xml.Append($"<mergeCell ref=\"{merge}\"/>");
                xml.Append("</mergeCells>");
            }
            xml.Append("</worksheet>");
            WriteEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", xml.ToString());
        }
        return path;
    }

    /// <summary>写 CSV（带 UTF-8 BOM，中文字段安全）。</summary>
    public static string WriteCsv(string fileName, params string[][] rows)
    {
        var dir = TempDir();
        var path = Path.Combine(dir, fileName);
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string EscapeCsv(string? value)
    {
        var v = value ?? "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>找仓库根目录（从测试输出目录向上找 Banji.sln）。</summary>
    public static string? TryFindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Banji.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

// ============================================================================
// 课表导入（4 种布局）测试
// ============================================================================

public static class TimetableImportTests
{
    [BanjiTest]
    public static void 布局1_星期为列_节次与时间在首列_csv()
    {
        var path = TestFileBuilder.WriteCsv("layout-a.csv",
            new[] { "节次", "时间", "周一", "周二", "周三", "周四", "周五" },
            new[] { "第1节", "08:00-08:45", "语文", "数学", "英语", "物理", "化学" },
            new[] { "第2节", "08:55-09:40", "数学", "英语", "语文", "生物", "历史" },
            new[] { "第3节", "10:00-10:45", "英语", "语文", "数学", "化学", "物理" },
            new[] { "第4节", "10:55-11:40", "物理", "数学", "语文", "英语", "生物" },
            new[] { "午休", "11:40-14:00", "", "", "", "", "" },
            new[] { "第5节", "14:00-14:45", "化学", "历史", "物理", "体育", "语文" },
            new[] { "第6节", "14:55-15:40", "体育", "体育", "信息技术", "数学", "英语" },
            new[] { "第7节", "15:50-16:35", "思想政治", "物理", "音乐", "语文", "数学" },
            new[] { "第8节", "16:45-17:30", "历史", "地理", "自习", "化学", "班会" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, "布局 1 应识别成功；warnings=" + string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), tt.Periods[0].Start);
        Assert.Equal(new TimeSpan(8, 45, 0), tt.Periods[0].End);
        Assert.Equal(new TimeSpan(16, 45, 0), tt.Periods[^1].Start);
        Assert.Equal(5, tt.Days.Count);
        Assert.Equal(40, tt.Days.Values.Sum(d => d.Count));
        Assert.Equal("语文", tt.CourseAt(DayOfWeek.Monday, 1));
        Assert.Equal("班会", tt.CourseAt(DayOfWeek.Friday, 8));
        Assert.True(tt.Breaks.Any(b => b.Name == "午休" && b.Start == new TimeSpan(11, 40, 0) && b.End == new TimeSpan(14, 0, 0)),
            "应识别午休 11:40-14:00");
        Assert.True(preview.FormatDescription.Contains("星期"), "格式说明应包含布局描述：" + preview.FormatDescription);
    }

    [BanjiTest]
    public static void 布局2_星期为行_节次为列_xlsx()
    {
        var sheet = new XlsxSheet { Name = "课程表" };
        sheet.Row("星期", "第1节", "第2节", "第3节", "第4节", "第5节", "第6节", "第7节", "第8节");
        sheet.Row("周一", "语文", "数学", "英语", "物理", "化学", "体育", "思想政治", "历史");
        sheet.Row("周二", "数学", "英语", "语文", "生物", "历史", "体育", "物理", "地理");
        sheet.Row("周三", "英语", "语文", "数学", "化学", "物理", "信息技术", "音乐", "自习");
        sheet.Row("周四", "物理", "数学", "语文", "英语", "生物", "体育", "化学", "历史");
        sheet.Row("周五", "语文", "英语", "数学", "思想政治", "地理", "美术", "班会", "生涯规划");
        var path = TestFileBuilder.WriteXlsx("layout-b.xlsx", sheet);

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, "布局 2 应识别成功；warnings=" + string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(5, tt.Days.Count);
        Assert.Equal(40, tt.Days.Values.Sum(d => d.Count));
        Assert.Equal("数学", tt.CourseAt(DayOfWeek.Tuesday, 1));
        Assert.Equal("生涯规划", tt.CourseAt(DayOfWeek.Friday, 8));
        // 没有时间的节次表：应自动推算（8:00 起，45 分钟一节，课间 10 分钟）
        Assert.Equal(new TimeSpan(8, 0, 0), tt.Periods[0].Start);
        Assert.Equal(new TimeSpan(9, 50, 0), tt.Periods[2].Start);
        Assert.True(preview.Warnings.Any(w => w.Contains("推算")), "缺少时间时应给出推算 warning");
        Assert.True(preview.FormatDescription.Contains("星期纵向") || preview.FormatDescription.Contains("星期为行") || preview.FormatDescription.Contains("节次为列"),
            "格式说明：" + preview.FormatDescription);
    }

    [BanjiTest]
    public static void 布局3_长表每行一节_csv()
    {
        var rows = new List<string[]> { new[] { "星期", "节次", "课程", "开始时间", "结束时间" } };
        var courses = new Dictionary<string, string[]>
        {
            ["周一"] = new[] { "语文", "数学", "英语", "物理", "化学", "体育", "思想政治", "历史" },
            ["周二"] = new[] { "数学", "英语", "语文", "生物", "历史", "体育", "物理", "地理" },
            ["周三"] = new[] { "英语", "语文", "数学", "化学", "物理", "信息技术", "音乐", "自习" },
            ["周四"] = new[] { "物理", "数学", "语文", "英语", "生物", "体育", "化学", "历史" },
            ["周五"] = new[] { "语文", "英语", "数学", "思想政治", "地理", "美术", "班会", "生涯规划" }
        };
        var starts = new[] { "08:00", "08:55", "10:00", "10:55", "14:00", "14:55", "15:50", "16:45" };
        var ends = new[] { "08:45", "09:40", "10:45", "11:40", "14:45", "15:40", "16:35", "17:30" };
        foreach (var (day, list) in courses)
        {
            for (var i = 0; i < list.Length; i++)
            {
                rows.Add(new[] { day, $"第{i + 1}节", list[i], starts[i], ends[i] });
            }
        }
        var path = TestFileBuilder.WriteCsv("layout-c.csv", rows.ToArray());

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, "布局 3 应识别成功；warnings=" + string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), tt.Periods[0].Start);
        Assert.Equal(new TimeSpan(17, 30, 0), tt.Periods[^1].End);
        Assert.Equal(5, tt.Days.Count);
        Assert.Equal(40, tt.Days.Values.Sum(d => d.Count));
        Assert.Equal("地理", tt.CourseAt(DayOfWeek.Tuesday, 8));
        Assert.True(preview.FormatDescription.Contains("长表"), "格式说明：" + preview.FormatDescription);
    }

    [BanjiTest]
    public static void 布局4_表头带时间范围_xlsx()
    {
        var header = new List<string> { "节次", "08:00-08:45", "08:55-09:40", "10:00-10:45", "10:55-11:40", "14:00-14:45", "14:55-15:40", "15:50-16:35", "16:45-17:30" };
        var sheet = new XlsxSheet { Name = "时间表头" };
        sheet.Row(header.ToArray());
        sheet.Row("周一", "语文", "数学", "英语", "物理", "化学", "体育", "思想政治", "历史");
        sheet.Row("周二", "数学", "英语", "语文", "生物", "历史", "体育", "物理", "地理");
        sheet.Row("周三", "英语", "语文", "数学", "化学", "物理", "信息技术", "音乐", "自习");
        sheet.Row("周四", "物理", "数学", "语文", "英语", "生物", "体育", "化学", "历史");
        sheet.Row("周五", "语文", "英语", "数学", "思想政治", "地理", "美术", "班会", "生涯规划");
        var path = TestFileBuilder.WriteXlsx("layout-d.xlsx", sheet);

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, "布局 4 应识别成功；warnings=" + string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), tt.Periods[0].Start);
        Assert.Equal(new TimeSpan(8, 45, 0), tt.Periods[0].End);
        Assert.Equal(new TimeSpan(14, 0, 0), tt.Periods[4].Start);
        Assert.Equal(40, tt.Days.Values.Sum(d => d.Count));

        // 11:40 ~ 14:00 的空档应自动识别为午休
        Assert.True(tt.Breaks.Any(b => b.Name == "午休" && b.Start == new TimeSpan(11, 40, 0) && b.End == new TimeSpan(14, 0, 0)),
            "应自动识别午休：" + string.Join("，", tt.Breaks.Select(b => $"{b.Name} {b.TimeText}")));
        Assert.True(preview.FormatDescription.Contains("时间范围"), "格式说明：" + preview.FormatDescription);
    }

    [BanjiTest]
    public static void 布局1_Excel合并单元格与分开的开始结束时间_xlsx()
    {
        // 首列“上午/下午”为合并单元格；开始/结束时间分列且为 Excel 时间格式（数字单元格）
        var sheet = new XlsxSheet { Name = "合并课表" };
        sheet.Row("", "节次", "开始时间", "结束时间", "周一", "周二", "周三", "周四", "周五");
        sheet.Row("上午", "第1节", "TIME:08:00", "TIME:08:45", "语文", "数学", "英语", "物理", "化学");
        sheet.Row("上午", "第2节", "TIME:08:55", "TIME:09:40", "数学", "英语", "语文", "生物", "历史");
        sheet.Row("上午", "第3节", "TIME:10:00", "TIME:10:45", "英语", "语文", "数学", "化学", "物理");
        sheet.Row("上午", "第4节", "TIME:10:55", "TIME:11:40", "物理", "数学", "语文", "英语", "生物");
        sheet.Row("下午", "第5节", "TIME:14:00", "TIME:14:45", "化学", "历史", "物理", "体育", "语文");
        sheet.Row("下午", "第6节", "TIME:14:55", "TIME:15:40", "体育", "体育", "信息技术", "数学", "英语");
        sheet.Row("下午", "第7节", "TIME:15:50", "TIME:16:35", "思想政治", "物理", "音乐", "语文", "数学");
        sheet.Row("下午", "第8节", "TIME:16:45", "TIME:17:30", "历史", "地理", "自习", "化学", "班会");
        sheet.Merge("A2:A5"); // 上午
        sheet.Merge("A6:A9"); // 下午
        var path = TestFileBuilder.WriteXlsx("layout-a-merged.xlsx", sheet);

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, "合并单元格课表应识别成功；warnings=" + string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), tt.Periods[0].Start);
        Assert.Equal(new TimeSpan(8, 45, 0), tt.Periods[0].End);
        Assert.Equal(40, tt.Days.Values.Sum(d => d.Count));
        Assert.Equal("化学", tt.CourseAt(DayOfWeek.Monday, 5));
        Assert.True(tt.Breaks.Any(b => b.Start == new TimeSpan(11, 40, 0) && b.End == new TimeSpan(14, 0, 0)), "应自动识别午休");
    }

    [BanjiTest]
    public static void 无法识别的文件只返回警告不崩溃()
    {
        var path = TestFileBuilder.WriteCsv("roster-like.csv",
            new[] { "学号", "姓名", "成绩" },
            new[] { "20260101", "王浩然", "95" },
            new[] { "20260102", "李思远", "93" });

        var preview = TimetableImporter.Preview(path);
        Assert.Null(preview.Candidate);
        Assert.True(preview.Warnings.Count > 0, "应有中文 warning");
        Assert.True(preview.Warnings.Any(w => w.Contains("未能识别") || w.Contains("星期")), string.Join(" / ", preview.Warnings));

        var missing = TimetableImporter.Preview(Path.Combine(Path.GetTempPath(), "banji-not-exist-" + Guid.NewGuid().ToString("N") + ".xlsx"));
        Assert.Null(missing.Candidate);
        Assert.True(missing.Warnings.Any(w => w.Contains("不存在") || w.Contains("文件")), string.Join(" / ", missing.Warnings));
    }

    [BanjiTest]
    public static void 多工作表自动选择识别效果最好的()
    {
        var garbage = new XlsxSheet { Name = "说明" };
        garbage.Row("这是说明页", "没有课表");
        garbage.Row("随便写点东西");

        var valid = new XlsxSheet { Name = "高一(3)班课表" };
        valid.Row("星期", "第1节", "第2节");
        valid.Row("周一", "语文", "数学");
        valid.Row("周二", "数学", "英语");
        valid.Row("周三", "英语", "物理");
        var path = TestFileBuilder.WriteXlsx("multi-sheet.xlsx", garbage, valid);

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate);
        Assert.Equal(2, preview.Sheets.Count);
        Assert.Equal("高一(3)班课表", preview.SuggestedSheet);
        Assert.Equal(3, preview.Candidate!.Days.Count);
        Assert.True(preview.Warnings.Any(w => w.Contains("自动选择")), string.Join(" / ", preview.Warnings));

        // 指定 sheet 名预览
        var bySheet = TimetableImporter.Preview(path, "说明");
        Assert.Null(bySheet.Candidate);
        Assert.Equal("说明", bySheet.SuggestedSheet);
    }

    [BanjiTest]
    public static void 打包样例课程表示例四种布局都能识别()
    {
        var root = TestFileBuilder.TryFindRepoRoot();
        if (root is null) return; // 找不到仓库根目录时跳过（不误报失败）
        var sample = Path.Combine(root, "src", "Banji", "Assets", "Templates", "课程表示例.xlsx");
        if (!File.Exists(sample)) return;

        var workbookPreview = TimetableImporter.Preview(sample);
        Assert.True(workbookPreview.Sheets.Count >= 4, "样例应有 4 个 sheet，实际：" + workbookPreview.Sheets.Count);
        Assert.NotNull(workbookPreview.Candidate, "样例应能识别；warnings=" + string.Join(" / ", workbookPreview.Warnings));

        var recognized = 0;
        foreach (var sheet in workbookPreview.Sheets)
        {
            var single = TimetableImporter.Preview(sample, sheet);
            if (single.Candidate is not null) recognized++;
        }
        Assert.Equal(workbookPreview.Sheets.Count, recognized, "每个 sheet 都应能识别出课表");
    }
}
