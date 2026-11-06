using System.IO.Compression;
using System.Text;
using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Rosters;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>花名册服务 / 导入识别的自动化测试。</summary>
public static class RosterTests
{
    private static readonly DateTime FixedNow = new(2026, 10, 30, 8, 0, 0);

    // =========================================================================
    // 花名册 CRUD / 激活 / 持久化
    // =========================================================================

    [BanjiTest]
    public static void 花名册_增删改查与持久化round_trip()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var file = ws.File("rosters.json");
        var svc = new RosterService(settings, file, () => FixedNow);

        var roster = svc.CreateManual("高一(3)班", new[]
        {
            new Student { Id = "20260101", Name = "王浩然", Score = 95 },
            new Student { Name = "李思远" },
            new Student { Id = "20260103" },
            new Student { Id = "   ", Name = "   " } // 非法行：姓名学号都空，应被过滤
        }, "数学");

        Assert.Equal(3, roster.Students.Count, "无效学生行应被过滤");
        Assert.Equal(1, svc.Rosters.Count);
        Assert.Equal("数学", roster.Subject);

        svc.SetActive(roster.Id);
        Assert.NotNull(svc.Active);
        Assert.Equal(roster.Id, svc.Active!.Id);

        // 重新加载：从 rosters.json 恢复
        var svc2 = new RosterService(settings, file, () => FixedNow);
        Assert.Equal(1, svc2.Rosters.Count);
        var loaded = svc2.Rosters[0];
        Assert.Equal("高一(3)班", loaded.Name);
        Assert.Equal(3, loaded.Students.Count);
        Assert.Equal<double?>(95, loaded.Students[0].Score);
        Assert.Equal(roster.Id, svc2.Active!.Id, "激活花名册应通过 settings 恢复");

        // 更新
        loaded.Name = "高一(3)班（改）";
        loaded.SortMode = RosterSortMode.Score;
        svc2.Update(loaded);
        Assert.Equal("高一(3)班（改）", svc2.Get(roster.Id)!.Name);

        // 删除
        svc2.Remove(roster.Id);
        Assert.Equal(0, svc2.Rosters.Count);
        Assert.Null(svc2.Active);
    }

    [BanjiTest]
    public static void 花名册_删除当前花名册后自动回退()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var a = svc.CreateManual("A 班", new[] { new Student { Name = "甲" } });
        var b = svc.CreateManual("B 班", new[] { new Student { Name = "乙" } });
        svc.SetActive(b.Id);
        Assert.Equal(b.Id, svc.Active!.Id);

        svc.Remove(b.Id);
        Assert.Equal(1, svc.Rosters.Count);
        Assert.NotNull(svc.Active);
        Assert.Equal(a.Id, svc.Active!.Id, "删除激活花名册后应回退到剩余的第一份");
    }

    [BanjiTest]
    public static void 花名册_排序_原始_成绩_姓名_学号自然序()
    {
        var roster = new Roster
        {
            Name = "排序测试",
            Students = new List<Student>
            {
                new() { Id = "10", Name = "王五", Score = 80 },
                new() { Id = "2", Name = "李四", Score = 95 },
                new() { Id = "1", Name = "阿明", Score = 70 },
                new() { Name = "无成绩" }
            }
        };

        roster.SortMode = RosterSortMode.Original;
        Assert.Equal("王五", roster.SortedStudents()[0].Name, "原始顺序应保持导入顺序");

        roster.SortMode = RosterSortMode.Score;
        var byScore = roster.SortedStudents();
        Assert.Equal("李四", byScore[0].Name);
        Assert.Equal("王五", byScore[1].Name);
        Assert.Equal("阿明", byScore[2].Name);
        Assert.Equal("无成绩", byScore[3].Name, "无成绩者排最后");

        roster.SortMode = RosterSortMode.StudentId;
        var byId = roster.SortedStudents();
        Assert.Equal("阿明", byId[0].Name, "学号 1 应排在 2/10 之前（自然序）");
        Assert.Equal("李四", byId[1].Name);
        Assert.Equal("王五", byId[2].Name);
        Assert.Equal("无成绩", byId[3].Name);

        roster.SortMode = RosterSortMode.Name;
        var byName = roster.SortedStudents();
        Assert.Equal("阿明", byName[0].Name, "zh-CN 拼音序：a");
        Assert.Equal("李四", byName[1].Name, "zh-CN 拼音序：li");
        Assert.Equal("王五", byName[2].Name, "zh-CN 拼音序：wang");
        Assert.Equal("无成绩", byName[3].Name, "zh-CN 拼音序：wu");
    }

    [BanjiTest]
    public static void 花名册_显示模式覆盖往返持久化()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var file = ws.File("rosters.json");
        var svc = new RosterService(settings, file, () => FixedNow);
        var roster = svc.CreateManual("D 班", new[] { new Student { Id = "1", Name = "张三" } });
        roster.DisplayModeOverride = PickDisplayMode.Name;
        svc.Update(roster);

        var reloaded = new RosterService(settings, file, () => FixedNow);
        Assert.Equal(PickDisplayMode.Name, reloaded.Get(roster.Id)!.DisplayModeOverride);

        // 清空覆盖后应恢复“跟随全局”（null）
        reloaded.Get(roster.Id)!.DisplayModeOverride = null;
        reloaded.Update(reloaded.Get(roster.Id)!);
        var reloaded2 = new RosterService(settings, file, () => FixedNow);
        Assert.Null(reloaded2.Get(roster.Id)!.DisplayModeOverride);
    }

    [BanjiTest]
    public static void 花名册_手动录入姓名或学号至少一个()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);

        var roster = svc.CreateManual("手动班", new[]
        {
            new Student { Id = "20260101", Name = "王浩然" },
            new Student { Id = "20260101", Name = "重复学号" }, // 学号重复 -> 去重
            new Student { Name = "只有姓名" },
            new Student { Id = "20260109" },                    // 只有学号也合法
            new Student { Name = "", Id = "" }                  // 都空 -> 过滤
        });

        Assert.Equal(3, roster.Students.Count);
        Assert.True(roster.Students.Any(s => s.Id == "20260109" && s.Name.Length == 0));
        Assert.True(roster.Students.Any(s => s.Id.Length == 0 && s.Name == "只有姓名"));
    }

    // =========================================================================
    // 导入：CSV / TSV / XLSX / 无表头 / 旧 xls
    // =========================================================================

    [BanjiTest]
    public static void 导入_CSV_自动识别表头与三列()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("高一3班名单.csv");
        File.WriteAllText(csv,
            "学号,姓名,成绩\r\n" +
            "20260101,王浩然,95\r\n" +
            "20260102,李思远,88.5\r\n" +
            "20260103,张雨桐,\r\n",
            new UTF8Encoding(false));

        var preview = svc.PreviewImport(csv);
        Assert.Equal("高一3班名单", preview.SuggestedRosterName, "建议花名册名应取文件名");
        Assert.Equal(0, preview.SuggestedHeaderRow, "应识别到第 1 行为表头");
        Assert.Equal(0, preview.SuggestedIdColumn);
        Assert.Equal(1, preview.SuggestedNameColumn);
        Assert.Equal(2, preview.SuggestedScoreColumn);
        Assert.Equal(3, preview.EstimatedRows);
        Assert.Equal(3, preview.SampleRows.Count);
        Assert.Equal("王浩然", preview.SampleRows[0][1]);
        Assert.True(preview.Warning is null || !preview.Warning.Contains("没有自动识别"), "不应有识别失败警告");

        var roster = svc.Import(csv, new RosterImportOptions
        {
            RosterName = preview.SuggestedRosterName,
            SheetName = preview.SuggestedSheet,
            HeaderRow = preview.SuggestedHeaderRow,
            IdColumn = preview.SuggestedIdColumn,
            NameColumn = preview.SuggestedNameColumn,
            ScoreColumn = preview.SuggestedScoreColumn
        });

        Assert.Equal(3, roster.Students.Count);
        Assert.Equal("王浩然", roster.Students[0].Name);
        Assert.Equal("20260101", roster.Students[0].Id);
        Assert.Equal<double?>(95, roster.Students[0].Score);
        Assert.Equal<double?>(88.5, roster.Students[1].Score);
        Assert.Null(roster.Students[2].Score);
        Assert.Equal(1, svc.Rosters.Count);
        Assert.NotNull(svc.Active, "导入后应可以拿到激活花名册（AppServices 会回退第一份）");
    }

    [BanjiTest]
    public static void 导入_CSV_无表头自动推断三列()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("无表头.csv");
        File.WriteAllText(csv,
            "20260101,王浩然,95\r\n" +
            "20260102,李思远,88.5\r\n" +
            "20260103,张雨桐,76\r\n",
            new UTF8Encoding(false));

        var preview = svc.PreviewImport(csv);
        Assert.Equal(-1, preview.SuggestedHeaderRow, "无表头文件应识别为 -1");
        Assert.Equal(0, preview.SuggestedIdColumn, "整列 8 位数字应推断为学号");
        Assert.Equal(1, preview.SuggestedNameColumn, "整列中文姓名应推断为姓名");
        Assert.Equal(2, preview.SuggestedScoreColumn, "0~150 数值列应推断为成绩");

        var roster = svc.Import(csv, new RosterImportOptions
        {
            HeaderRow = -1,
            IdColumn = 0,
            NameColumn = 1,
            ScoreColumn = 2
        });
        Assert.Equal(3, roster.Students.Count);
        Assert.Equal("王浩然", roster.Students[0].Name);
        Assert.Equal("20260101", roster.Students[0].Id);
    }

    [BanjiTest]
    public static void 导入_无表头_姓名加成绩不会被误判成学号()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("只有姓名成绩.csv");
        File.WriteAllText(csv, "王浩然,95\r\n李思远,88\r\n张雨桐,76\r\n", new UTF8Encoding(false));

        var preview = svc.PreviewImport(csv);
        Assert.Equal(0, preview.SuggestedNameColumn);
        Assert.Null(preview.SuggestedIdColumn, "分散的分数不应被当成学号");
        Assert.Equal(1, preview.SuggestedScoreColumn);
    }

    [BanjiTest]
    public static void 导入_TSV_制表符分隔()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var tsv = ws.File("名单.tsv");
        File.WriteAllText(tsv, "姓名\t学号\t成绩\r\n王浩然\t20260101\t95\r\n李思远\t20260102\t88\r\n", new UTF8Encoding(false));

        var preview = svc.PreviewImport(tsv);
        Assert.Equal(0, preview.SuggestedNameColumn);
        Assert.Equal(1, preview.SuggestedIdColumn);
        Assert.Equal(2, preview.SuggestedScoreColumn);
        Assert.Equal(2, preview.EstimatedRows);

        var roster = svc.Import(tsv, new RosterImportOptions
        {
            HeaderRow = preview.SuggestedHeaderRow,
            NameColumn = preview.SuggestedNameColumn,
            IdColumn = preview.SuggestedIdColumn,
            ScoreColumn = preview.SuggestedScoreColumn
        });
        Assert.Equal(2, roster.Students.Count);
        Assert.Equal("王浩然", roster.Students[0].Name);
    }

    [BanjiTest]
    public static void 导入_XLSX_多工作表自动选名单页()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var xlsx = ws.File("高二1班.xlsx");
        CreateXlsx(xlsx,
            ("说明", new[] { new[] { "这个表是说明" } }),
            ("学生名单", new[]
            {
                new[] { "姓名", "学号", "成绩" },
                new[] { "王浩然", "20260101", "95" },
                new[] { "李思远", "20260102", "88.5" },
                new[] { "张雨桐", "20260103", "76" }
            }));

        var preview = svc.PreviewImport(xlsx);
        Assert.Equal(2, preview.Sheets.Count);
        Assert.Equal("学生名单", preview.SuggestedSheet, "应跳过说明页，选有名单的工作表");
        Assert.Equal(0, preview.SuggestedHeaderRow);
        Assert.Equal(1, preview.SuggestedIdColumn);
        Assert.Equal(0, preview.SuggestedNameColumn);
        Assert.Equal(2, preview.SuggestedScoreColumn);
        Assert.Equal(3, preview.EstimatedRows);
        Assert.Equal("高二1班", preview.SuggestedRosterName);

        var roster = svc.Import(xlsx, new RosterImportOptions
        {
            RosterName = "高二1班（数学）",
            SheetName = "学生名单",
            HeaderRow = 0,
            NameColumn = 0,
            IdColumn = 1,
            ScoreColumn = 2,
            SortMode = RosterSortMode.Score
        });
        Assert.Equal("高二1班（数学）", roster.Name);
        Assert.Equal(3, roster.Students.Count);
        Assert.Equal(RosterSortMode.Score, roster.SortMode);
        Assert.Equal("王浩然", roster.SortedStudents()[0].Name);
    }

    [BanjiTest]
    public static void 导入_去重并过滤空行()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("重复.csv");
        File.WriteAllText(csv,
            "学号,姓名,成绩\r\n" +
            "20260101,王浩然,95\r\n" +
            ",,\r\n" +
            "20260101,王浩然,95\r\n" +   // 学号重复 -> 跳过
            "20260102,李思远,80\r\n" +
            "20260103,,70\r\n" +         // 只有学号也保留
            ",王浩然,60\r\n",            // 姓名已出现过（但学号不同）-> 作为独立学生保留
            new UTF8Encoding(false));

        var preview = svc.PreviewImport(csv);
        var roster = svc.Import(csv, new RosterImportOptions
        {
            HeaderRow = preview.SuggestedHeaderRow,
            IdColumn = 0,
            NameColumn = 1,
            ScoreColumn = 2
        });

        Assert.Equal(4, roster.Students.Count, "空行过滤 + 重复学号去重");
        Assert.Equal(1, roster.Students.Count(s => s.Id == "20260101"));
    }

    [BanjiTest]
    public static void 导入_旧版XLS给出友好中文提示()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var xls = ws.File("旧名单.xls");
        File.WriteAllBytes(xls, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });

        var ex = Assert.Throws<InvalidOperationException>(() => svc.PreviewImport(xls));
        Assert.True(ex.Message.Contains("xlsx"), "提示里应告诉用户另存为 .xlsx");
        Assert.True(ex.Message.Contains("另存为"), "提示应为友好中文");
    }

    [BanjiTest]
    public static void 导入_未映射姓名或学号时抛中文异常()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("只有成绩.csv");
        File.WriteAllText(csv, "成绩\r\n95\r\n88\r\n", new UTF8Encoding(false));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.Import(csv, new RosterImportOptions { HeaderRow = 0, ScoreColumn = 0 }));
        Assert.True(ex.Message.Contains("姓名") && ex.Message.Contains("学号"), "提示应说明必须选择姓名或学号列");
    }

    [BanjiTest]
    public static void 导入_XLSM_与XLSX走同一套识别()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var xlsx = ws.File("源文件.xlsx");
        CreateXlsx(xlsx, ("名单", new[]
        {
            new[] { "学号", "姓名" },
            new[] { "20260101", "王浩然" },
            new[] { "20260102", "李思远" }
        }));
        var xlsm = ws.File("宏工作簿.xlsm");
        File.Copy(xlsx, xlsm, overwrite: true);

        var preview = svc.PreviewImport(xlsm);
        Assert.Equal(2, preview.EstimatedRows);
        Assert.Equal(0, preview.SuggestedIdColumn);
        Assert.Equal(1, preview.SuggestedNameColumn);

        var roster = svc.Import(xlsm, new RosterImportOptions
        {
            HeaderRow = preview.SuggestedHeaderRow,
            IdColumn = 0,
            NameColumn = 1
        });
        Assert.Equal(2, roster.Students.Count);
    }

    [BanjiTest]
    public static void 导入_TXT_自动识别分隔符()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var txt = ws.File("分号名单.txt");
        File.WriteAllText(txt,
            "学号;姓名;成绩\r\n20260101;王浩然;95\r\n20260102;李思远;88\r\n",
            new UTF8Encoding(false));

        var preview = svc.PreviewImport(txt);
        Assert.Equal(0, preview.SuggestedIdColumn);
        Assert.Equal(1, preview.SuggestedNameColumn);
        Assert.Equal(2, preview.SuggestedScoreColumn);
        Assert.Equal(2, preview.EstimatedRows);
    }

    [BanjiTest]
    public static void 导入_只识别到成绩列时给出警告()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var csv = ws.File("只有成绩.csv");
        File.WriteAllText(csv, "成绩\r\n95\r\n88\r\n76\r\n", new UTF8Encoding(false));

        var preview = svc.PreviewImport(csv);
        Assert.NotNull(preview.Warning, "缺少姓名/学号列时应给出警告");
        Assert.True(preview.Warning!.Contains("姓名") || preview.Warning.Contains("学号"));
        Assert.Null(preview.SuggestedNameColumn);
        Assert.Null(preview.SuggestedIdColumn);
        Assert.Equal(0, preview.SuggestedScoreColumn);
    }

    // =========================================================================
    // 第二轮：课堂积分
    // =========================================================================

    [BanjiTest]
    public static void 积分_加减分持久化与排行榜()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var file = ws.File("rosters.json");
        var svc = new RosterService(settings, file, () => FixedNow);
        var roster = svc.CreateManual("积分班", new[]
        {
            new Student { Id = "20260101", Name = "王浩然", Score = 95 },
            new Student { Id = "20260102", Name = "李思远", Score = 88 },
            new Student { Id = "20260103", Name = "张雨桐", Score = 80 },
            new Student { Id = "20260104", Name = "刘一诺", Score = 70 }
        });
        svc.SetActive(roster.Id);

        var changed = 0;
        svc.Changed += (_, _) => changed++;

        svc.AddPoints(roster.Id, "20260101", 3);
        svc.AddPoints(roster.Id, "20260102", 1);
        svc.AddPoints(roster.Id, "20260103", 2);
        svc.AddPoints(roster.Id, "20260103", -1);
        Assert.Equal(4, changed, "每次加减分都应广播 Changed");

        var current = svc.Get(roster.Id)!;
        Assert.Equal(3, current.Students.First(s => s.Id == "20260101").Points);
        Assert.Equal(1, current.Students.First(s => s.Id == "20260102").Points);
        Assert.Equal(1, current.Students.First(s => s.Id == "20260103").Points);
        Assert.Equal(0, current.Students.First(s => s.Id == "20260104").Points);

        // 落盘并重新加载
        var reloaded = new RosterService(settings, file, () => FixedNow);
        Assert.Equal(3, reloaded.Get(roster.Id)!.Students.First(s => s.Id == "20260101").Points);

        // 排行榜：降序、含 0 分、count 截断
        var top = reloaded.TopPoints(roster.Id);
        Assert.Equal(4, top.Count);
        Assert.Equal("20260101", top[0].StudentId);
        Assert.Equal(3, top[0].Points);
        Assert.Equal(0, top[^1].Points, "0 分的学生也要出现在排行榜里");
        Assert.Equal(2, reloaded.TopPoints(roster.Id, 2).Count);
        Assert.Equal(0, reloaded.TopPoints(roster.Id, 0).Count);
        Assert.Equal(0, reloaded.TopPoints("不存在的名单", 5).Count);
    }

    [BanjiTest]
    public static void 积分_清零与非法参数校验()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var roster = svc.CreateManual("清零班", new[]
        {
            new Student { Id = "1", Name = "甲" },
            new Student { Id = "2", Name = "乙" }
        });

        svc.AddPoints(roster.Id, "1", 2);
        svc.AddPoints(roster.Id, "2", 3);
        Assert.True(svc.Get(roster.Id)!.Students.Any(s => s.Points > 0));

        svc.ResetPoints(roster.Id);
        Assert.True(svc.Get(roster.Id)!.Students.All(s => s.Points == 0), "清零后所有学生积分应为 0");
        svc.ResetPoints(roster.Id); // 已经是 0，再清零不应抛异常

        // 非法参数：友好中文提示
        var missingRoster = Assert.Throws<InvalidOperationException>(() => svc.AddPoints("不存在", "1", 1));
        Assert.True(missingRoster.Message.Contains("花名册"));
        var missingStudent = Assert.Throws<InvalidOperationException>(() => svc.AddPoints(roster.Id, "不存在", 1));
        Assert.True(missingStudent.Message.Contains("学生"));

        // delta = 0 无副作用；超大 delta 会被收敛到 ±1000
        svc.AddPoints(roster.Id, "1", 0);
        Assert.Equal(0, svc.Get(roster.Id)!.Students.First(s => s.Id == "1").Points);
        svc.AddPoints(roster.Id, "1", 5000);
        Assert.Equal(1000, svc.Get(roster.Id)!.Students.First(s => s.Id == "1").Points);
        svc.AddPoints(roster.Id, "1", -5000);
        Assert.Equal(0, svc.Get(roster.Id)!.Students.First(s => s.Id == "1").Points);
    }

    [BanjiTest]
    public static void 花名册_旧版JSON没有积分字段也能兼容()
    {
        using var ws = new TempWorkspace();
        var file = ws.File("rosters.json");
        // 第一轮格式：Student 没有 points 字段
        var legacyJson =
            "[{\"id\":\"r-old\",\"name\":\"旧版花名册\",\"subject\":\"数学\",\"sortMode\":\"score\"," +
            "\"students\":[{\"id\":\"20260101\",\"name\":\"张三\",\"score\":90}," +
            "{\"id\":\"20260102\",\"name\":\"李四\"}]}]";
        File.WriteAllText(file, legacyJson, new UTF8Encoding(false));

        var settings = new FakeSettingsService();
        var svc = new RosterService(settings, file, () => FixedNow);
        Assert.Equal(1, svc.Rosters.Count);
        var roster = svc.Get("r-old")!;
        Assert.Equal(2, roster.Students.Count);
        Assert.True(roster.Students.All(s => s.Points == 0), "旧 JSON 缺少积分字段时应默认为 0");
        Assert.Equal(RosterSortMode.Score, roster.SortMode, "旧 JSON 的排序模式应正常反序列化");

        svc.AddPoints("r-old", "20260101", 5);
        var reloaded = new RosterService(settings, file, () => FixedNow);
        Assert.Equal(5, reloaded.Get("r-old")!.Students.First(s => s.Id == "20260101").Points);
    }

    // =========================================================================
    // 内部：xlsx 生成（不依赖第三方库）
    // =========================================================================

    private static void CreateXlsx(string path, params (string Name, string[][] Rows)[] sheets)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var contentTypes = new StringBuilder();
        contentTypes.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        contentTypes.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        contentTypes.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        contentTypes.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");

        var workbook = new StringBuilder();
        workbook.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        workbook.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

        var workbookRels = new StringBuilder();
        workbookRels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        workbookRels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (var i = 0; i < sheets.Length; i++)
        {
            var index = i + 1;
            contentTypes.Append($"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            workbook.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{index}\" r:id=\"rId{index}\"/>");
            workbookRels.Append($"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>");
            AddEntry(zip, $"xl/worksheets/sheet{index}.xml", BuildSheetXml(sheets[i].Rows));
        }

        contentTypes.Append("</Types>");
        workbook.Append("</sheets></workbook>");
        workbookRels.Append("</Relationships>");

        AddEntry(zip, "[Content_Types].xml", contentTypes.ToString());
        AddEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        AddEntry(zip, "xl/workbook.xml", workbook.ToString());
        AddEntry(zip, "xl/_rels/workbook.xml.rels", workbookRels.ToString());
    }

    private static string BuildSheetXml(string[][] rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c] ?? "";
                if (value.Length == 0) continue;
                sb.Append($"<c r=\"{ColumnName(c)}{r + 1}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(value)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColumnName(int columnIndex)
    {
        var name = "";
        var n = columnIndex + 1;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            name = (char)('A' + rem) + name;
            n = (n - 1) / 26;
        }
        return name;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static void AddEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

/// <summary>测试用内存设置服务（不读写 %APPDATA%）。</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    private AppSettings _current = new();

    public AppSettings Current => _current;
    public event EventHandler? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        mutate(_current);
        _current.Normalize();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
    }

    public void ResetToDefaults()
    {
        _current = new AppSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>测试用临时目录，Dispose 时递归删除。</summary>
internal sealed class TempWorkspace : IDisposable
{
    public string Dir { get; }

    public TempWorkspace()
    {
        Dir = Path.Combine(Path.GetTempPath(), "banji-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结果
        }
    }
}
