using System.IO.Compression;
using System.Text;
using Banji.Core.IO;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>Lead 负责的共享基础设施测试（表格读取/JSON 存储/模型格式化）。</summary>
public static class InfrastructureTests
{
    [BanjiTest]
    public static void CSV解析_引号逗号换行与分隔符()
    {
        var csv = "\uFEFF学号,姓名,备注\r\n" +
                  "20260101,\"王浩然\",\"喜欢,数学\"\r\n" +
                  "20260102,\"李思远\",\"第一行\n第二行\"\r\n" +
                  "20260103,张雨桐,\r\n";
        var path = TempFile(".csv", csv);
        try
        {
            var wb = SpreadsheetReader.Load(path);
            var sheet = wb.Find("");
            Assert.NotNull(sheet);
            Assert.Equal(4, sheet!.RowCount);
            Assert.Equal("学号", sheet.Cell(0, 0));
            Assert.Equal("喜欢,数学", sheet.Cell(1, 2));
            Assert.Equal("第一行\n第二行", sheet.Cell(2, 2));
            Assert.Equal("20260103", sheet.Cell(3, 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [BanjiTest]
    public static void TSV和分号自动识别()
    {
        var tsv = TempFile(".tsv", "a\tb\tc\n1\t2\t3\n");
        var semicolon = TempFile(".csv", "a;b;c\n1;2;3\n");
        try
        {
            var t = SpreadsheetReader.Load(tsv).Find(null)!;
            Assert.Equal("b", t.Cell(0, 1));
            Assert.Equal("3", t.Cell(1, 2));

            var s = SpreadsheetReader.Load(semicolon).Find(null)!;
            Assert.Equal("a", s.Cell(0, 0));
            Assert.Equal("2", s.Cell(1, 1));
        }
        finally
        {
            File.Delete(tsv);
            File.Delete(semicolon);
        }
    }

    [BanjiTest]
    public static void Xlsx读取_多sheet共享字符串合并单元格与数字()
    {
        var path = TempFile(".xlsx", "");
        try
        {
            TestXlsx.Create(path,
                new[]
                {
                    ("花名册", new[]
                    {
                        new[] { "学号", "姓名", "成绩" },
                        new[] { "20260101", "王浩然", "95" },
                        new[] { "20260102", "李思远", "88.5" }
                    }),
                    ("课表", new[]
                    {
                        new[] { "节次", "星期一", "星期一" },
                        new[] { "1", "语文", "数学" }
                    })
                },
                merge: new[] { ("课表", "A1:B1") });

            var wb = SpreadsheetReader.Load(path);
            Assert.Equal(2, wb.Sheets.Count);
            Assert.Equal("花名册", wb.Sheets[0].Name);

            var roster = wb.Sheets[0];
            Assert.Equal("学号", roster.Cell(0, 0));
            Assert.Equal("王浩然", roster.Cell(1, 1));
            Assert.Equal("20260101", roster.Cell(1, 0));
            Assert.Equal("88.5", roster.Cell(2, 2));

            var tt = wb.Sheets[1];
            // 合并单元格：值为空时按左上角填充（A1:B1 合并）
            Assert.Equal("节次", tt.Cell(0, 1));
            Assert.Equal("语文", tt.Cell(1, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [BanjiTest]
    public static void Xlsx绝对路径Target也能读取()
    {
        var path = TempFile(".xlsx", "");
        try
        {
            // openpyxl/WPS 等工具常把 rels 的 Target 写成绝对路径 "/xl/worksheets/sheet1.xml"
            TestXlsx.Create(path, new[] { ("数据", new[] { new[] { "姓名", "学号" }, new[] { "王浩然", "20260101" } }) },
                absoluteTargets: true);
            var wb = SpreadsheetReader.Load(path);
            var sheet = wb.Find(null)!;
            Assert.Equal(2, sheet.RowCount);
            Assert.Equal("王浩然", sheet.Cell(1, 0));
            Assert.Equal("20260101", sheet.Cell(1, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [BanjiTest]
    public static void Xlsx旧格式与无效文件友好报错()
    {
        var path = TempFile(".xlsx", "not a zip");
        var xls = TempFile(".xls", "fake");
        try
        {
            Assert.Throws<InvalidDataException>(() => SpreadsheetReader.Load(path));
            Assert.Throws<NotSupportedException>(() => SpreadsheetReader.Load(xls));
        }
        finally
        {
            File.Delete(path);
            File.Delete(xls);
        }
    }

    [BanjiTest]
    public static void SafeJsonStore往返与损坏隔离()
    {
        var path = TempFile(".json", "");
        try
        {
            var settings = new AppSettings();
            settings.ActiveRosterId = "abc";
            settings.UiScale = 1.37;
            settings.Pet.BodyColor = "#D97757";
            SafeJsonStore.Save(path, settings);

            var loaded = SafeJsonStore.Load(path, () => new AppSettings());
            Assert.Equal("abc", loaded.ActiveRosterId);
            Assert.Equal(1.37, loaded.UiScale);
            Assert.Equal("#D97757", loaded.Pet.BodyColor);

            File.WriteAllText(path, "{ 这不是 json");
            var fallback = SafeJsonStore.Load(path, () => new AppSettings { UiScale = 2.0 });
            Assert.Equal(2.0, fallback.UiScale);
            Assert.False(File.Exists(path)); // 已隔离为 .corrupt-*
            Assert.True(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*").Length >= 1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path)!;
            foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + "*")) { try { File.Delete(f); } catch { } }
        }
    }

    [BanjiTest]
    public static void 倒计时格式化()
    {
        Assert.Equal("00:00", ScheduleSnapshot.FormatCountdown(TimeSpan.Zero));
        Assert.Equal("00:59", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(59)));
        Assert.Equal("01:00", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(59.2)));
        Assert.Equal("1:01:01", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(3661)));
        Assert.Equal("00:00", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(-5)));
    }

    [BanjiTest]
    public static void 花名册排序_姓名与学号自然序()
    {
        var roster = new Roster
        {
            Students = new List<Student>
            {
                new() { Id = "10", Name = "张三" },
                new() { Id = "2", Name = "李四" },
                new() { Id = "1", Name = "王五" }
            }
        };
        roster.SortMode = RosterSortMode.StudentId;
        var ids = roster.SortedStudents().Select(s => s.Id).ToArray();
        Assert.Equal("1", ids[0]);
        Assert.Equal("2", ids[1]);
        Assert.Equal("10", ids[2]);

        roster.SortMode = RosterSortMode.Name;
        var names = roster.SortedStudents().Select(s => s.Name).ToArray();
        Assert.Equal(3, names.Length);
    }

    // =========================================================================
    // 测试用 XLSX 生成器（真实 OOXML 包）
    // =========================================================================

    private static string TempFile(string ext, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "banji-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        if (content.Length > 0) File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static class TestXlsx
    {
        public static void Create(string path, (string Name, string[][] Rows)[] sheets, (string Sheet, string Ref)[]? merge = null, bool absoluteTargets = false)
        {
            if (File.Exists(path)) File.Delete(path);
            var strings = new List<string>();
            string SharedIndex(string s)
            {
                var idx = strings.IndexOf(s);
                if (idx < 0) { strings.Add(s); idx = strings.Count - 1; }
                return idx.ToString();
            }

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            Write(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(Enumerable.Range(1, sheets.Length).Select(i =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");

            Write(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            var workbook = new StringBuilder();
            workbook.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (var i = 0; i < sheets.Length; i++)
                workbook.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            workbook.Append("</sheets></workbook>");
            Write(zip, "xl/workbook.xml", workbook.ToString());

            var rels = new StringBuilder();
            rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Length; i++)
            {
                var worksheetsTarget = absoluteTargets ? $"/xl/worksheets/sheet{i + 1}.xml" : $"worksheets/sheet{i + 1}.xml";
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"{worksheetsTarget}\"/>");
            }
            rels.Append($"<Relationship Id=\"rId{sheets.Length + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            rels.Append($"<Relationship Id=\"rId{sheets.Length + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            rels.Append("</Relationships>");
            Write(zip, "xl/_rels/workbook.xml.rels", rels.ToString());

            var styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                "<borders count=\"1\"><border/></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
                "</styleSheet>";
            Write(zip, "xl/styles.xml", styles);

            for (var i = 0; i < sheets.Length; i++)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                var rows = sheets[i].Rows;
                for (var r = 0; r < rows.Length; r++)
                {
                    sb.Append($"<row r=\"{r + 1}\">");
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var value = rows[r][c];
                        if (string.IsNullOrEmpty(value)) continue;
                        var reference = Column(c) + (r + 1);
                        if (value.All(ch => char.IsDigit(ch)))
                            sb.Append($"<c r=\"{reference}\"><v>{value}</v></c>");
                        else
                            sb.Append($"<c r=\"{reference}\" t=\"s\"><v>{SharedIndex(value)}</v></c>");
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData>");
                if (merge is not null)
                {
                    var merges = merge.Where(m => m.Sheet == sheets[i].Name).ToList();
                    if (merges.Count > 0)
                    {
                        sb.Append($"<mergeCells count=\"{merges.Count}\">");
                        foreach (var m in merges) sb.Append($"<mergeCell ref=\"{m.Ref}\"/>");
                        sb.Append("</mergeCells>");
                    }
                }
                sb.Append("</worksheet>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
            }

            var sst = new StringBuilder();
            sst.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                $"count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
            foreach (var s in strings) sst.Append($"<si><t xml:space=\"preserve\">{Escape(s)}</t></si>");
            sst.Append("</sst>");
            Write(zip, "xl/sharedStrings.xml", sst.ToString());
        }

        private static string Column(int index)
        {
            var result = "";
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                result = (char)('A' + rem) + result;
                index = (index - 1) / 26;
            }
            return result;
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;");

        private static void Write(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
