using Banji.Core.Boot;
using Banji.Core.Models;

namespace Banji.Core.Infrastructure;

/// <summary>开发/QA 演示数据种子：只在数据为空时填充，方便截图与验收。</summary>
public static class DevSeed
{
    public static string Run()
    {
        var svc = AppServices.Current;
        var report = new List<string>();

        if (svc.Rosters.Rosters.Count == 0)
        {
            var names = new[]
            {
                "王浩然", "李思远", "张雨桐", "刘一诺", "陈子轩", "杨若曦", "赵嘉怡", "周明远",
                "吴亦菲", "徐子墨", "孙悦然", "马晨曦", "朱婉清", "胡俊杰", "郭静怡", "何思源",
                "高子涵", "林雨欣", "罗天宇", "梁诗琪", "宋佳明", "谢雨萱", "唐子豪", "韩雪晴",
                "冯逸辰", "曹语彤", "彭若尘", "曾可欣", "萧亦航", "田若溪"
            };
            var students = names.Select((n, i) => new Student
            {
                Id = (20260101 + i).ToString(),
                Name = n,
                Score = 95 - (i * 1.7)
            }).ToList();
            svc.Rosters.Add(new Roster
            {
                Name = "高一(3)班",
                Subject = "数学",
                SortMode = RosterSortMode.Score,
                Students = students
            });
            svc.Rosters.Add(new Roster
            {
                Name = "高一(3)班（语文）",
                Subject = "语文",
                SortMode = RosterSortMode.Name,
                Students = students.Select(s => s.Clone()).ToList()
            });
            svc.Rosters.SetActive(svc.Rosters.Rosters[0].Id);
            report.Add("已生成 2 份演示花名册");
        }

        var tt = svc.Timetable.Current;
        if (tt.Periods.Count > 0 && tt.Days.Values.SelectMany(v => v).All(e => string.IsNullOrWhiteSpace(e.Course)))
        {
            var week = new Dictionary<DayOfWeek, string[]>
            {
                [DayOfWeek.Monday] = new[] { "语文", "数学", "英语", "物理", "化学", "体育", "思想政治", "历史" },
                [DayOfWeek.Tuesday] = new[] { "数学", "英语", "语文", "生物", "历史", "体育", "物理", "地理" },
                [DayOfWeek.Wednesday] = new[] { "英语", "语文", "数学", "化学", "物理", "信息技术", "音乐", "自习" },
                [DayOfWeek.Thursday] = new[] { "物理", "数学", "语文", "英语", "生物", "体育", "化学", "历史" },
                [DayOfWeek.Friday] = new[] { "语文", "英语", "数学", "思想政治", "地理", "美术", "班会", "生涯规划" }
            };
            foreach (var (day, courses) in week)
            {
                for (var i = 0; i < courses.Length; i++)
                    svc.Timetable.SetCourse(day, i + 1, courses[i]);
            }
            report.Add("已生成演示课表");
        }

        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // 周末也造一份今天的临时课表，方便截图
            var day = now.DayOfWeek;
            svc.Timetable.SetCourse(day, 1, "语文");
            svc.Timetable.SetCourse(day, 2, "数学");
            svc.Timetable.SetCourse(day, 3, "英语");
            svc.Timetable.SetCourse(day, 4, "物理");
            report.Add("周末演示：已填充今天课表");
        }

        svc.Settings.Save();
        svc.Rosters.Save();
        svc.Timetable.Save();
        svc.Clock.Refresh();
        return string.Join("；", report);
    }
}
