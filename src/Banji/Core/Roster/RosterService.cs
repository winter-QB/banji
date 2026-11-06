using System.Globalization;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Rosters;

/// <summary>
/// 花名册服务：完整 CRUD、激活、导入、持久化（rosters.json）、Changed 事件。
/// 数量无上限；排序/显示模式由 <see cref="Roster"/> 自身字段表达，UI 通过 <see cref="Roster.SortedStudents"/> 取排序结果。
/// </summary>
public sealed class RosterService : IRosterService
{
    private readonly object _gate = new();
    private readonly List<Roster> _rosters = new();
    private readonly string _filePath;
    private readonly Func<DateTime> _now;

    private ISettingsService? _settings;
    private string _fallbackActiveId = "";

    /// <summary>宿主（AppServices）使用的构造函数；激活状态从 AppServices 的设置服务读取。</summary>
    public RosterService()
        : this(null, null, null)
    {
    }

    /// <summary>可测试构造函数：注入设置服务、数据文件路径与时间。</summary>
    public RosterService(ISettingsService? settings, string? filePath = null, Func<DateTime>? now = null)
    {
        _settings = settings;
        _filePath = filePath ?? AppPaths.RostersFile;
        _now = now ?? (() => DateTime.Now);
        LoadCore();
    }

    // =========================================================================
    // 契约实现
    // =========================================================================

    public IReadOnlyList<Roster> Rosters
    {
        get { lock (_gate) return _rosters.ToList(); }
    }

    public event EventHandler? Changed;

    public Roster? Get(string? rosterId)
    {
        if (string.IsNullOrWhiteSpace(rosterId)) return null;
        lock (_gate)
        {
            return _rosters.FirstOrDefault(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal));
        }
    }

    public Roster? Active
    {
        get
        {
            lock (_gate)
            {
                var byId = Get(ActiveRosterId);
                return byId ?? _rosters.FirstOrDefault();
            }
        }
    }

    public void SetActive(string? rosterId)
    {
        var normalized = rosterId?.Trim() ?? "";
        if (normalized.Length > 0 && Get(normalized) is null) return; // 目标不存在时忽略，避免激活状态悬空

        var settings = Settings;
        if (settings is null)
        {
            _fallbackActiveId = normalized;
            return;
        }
        settings.Update(s => s.ActiveRosterId = normalized);
    }

    public Roster Add(Roster roster)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        lock (_gate)
        {
            NormalizeRoster(roster);
            if (_rosters.Any(r => string.Equals(r.Id, roster.Id, StringComparison.Ordinal)))
                roster.Id = NewId();
            roster.CreatedAt = _now();
            roster.UpdatedAt = roster.CreatedAt;
            _rosters.Add(roster);
            SaveCore();
        }
        RaiseChanged();
        return roster;
    }

    public void Update(Roster roster)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        lock (_gate)
        {
            NormalizeRoster(roster);
            var index = _rosters.FindIndex(r => string.Equals(r.Id, roster.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                _rosters.Add(roster);
            }
            else
            {
                roster.UpdatedAt = _now();
                _rosters[index] = roster;
            }
            SaveCore();
        }
        RaiseChanged();
    }

    public void Remove(string rosterId)
    {
        if (string.IsNullOrWhiteSpace(rosterId)) return;
        string? nextActive = null;
        var changed = false;
        lock (_gate)
        {
            var wasActive = string.Equals(ActiveRosterId, rosterId, StringComparison.Ordinal);
            changed = _rosters.RemoveAll(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal)) > 0;
            if (!changed) return;
            if (wasActive) nextActive = _rosters.Count > 0 ? _rosters[0].Id : "";
            SaveCore();
        }
        if (nextActive is not null) SetActive(nextActive);
        RaiseChanged();
    }

    public Roster CreateManual(string name, IEnumerable<Student> students, string? subject = null)
    {
        var list = new List<Student>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var student in students ?? Enumerable.Empty<Student>())
        {
            if (student is null || !student.IsValid) continue;
            var clean = new Student
            {
                Id = (student.Id ?? "").Trim(),
                Name = (student.Name ?? "").Trim(),
                Score = student.Score,
                Note = string.IsNullOrWhiteSpace(student.Note) ? null : student.Note.Trim()
            };
            if (!seen.Add(clean.Key)) continue; // 同一次录入里重复的学号/姓名只保留一条
            list.Add(clean);
        }

        var roster = new Roster
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名花名册" : name.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            Students = list
        };
        return Add(roster);
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveCore();
        }
    }

    // =========================================================================
    // 积分（第二轮新增）
    // =========================================================================

    /// <summary>单次加减分的合理范围（防止误操作导致积分爆炸 / int 溢出）。</summary>
    private const int MaxPointsDelta = 1000;

    /// <summary>单名学生积分绝对值上限。</summary>
    private const int MaxPointsTotal = 1_000_000;

    public void AddPoints(string rosterId, string studentKey, int delta)
    {
        if (string.IsNullOrWhiteSpace(rosterId))
            throw new InvalidOperationException("请先选择一份花名册，再给学生加减积分。");
        if (string.IsNullOrWhiteSpace(studentKey))
            throw new InvalidOperationException("请先选择一名学生，再加分或减分。");
        if (delta == 0) return;

        // 防止异常大的 delta；正常 UI 只传 ±1
        delta = Math.Clamp(delta, -MaxPointsDelta, MaxPointsDelta);
        var key = studentKey.Trim();
        var name = "";
        var points = 0;

        lock (_gate)
        {
            var roster = _rosters.FirstOrDefault(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal))
                         ?? throw new InvalidOperationException("找不到要加减积分的学生所在花名册，可能已被删除。");
            var student = roster.Students.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal))
                          ?? throw new InvalidOperationException("找不到该学生，可能花名册刚被修改，请刷新后重试。");

            var next = (long)student.Points + delta;
            student.Points = (int)Math.Clamp(next, -MaxPointsTotal, MaxPointsTotal);
            roster.UpdatedAt = _now();
            points = student.Points;
            name = student.DisplayName;
            SaveCore();
        }

        Log.Info($"积分变更：{name} {delta:+#;-#;0} → {points}");
        RaiseChanged();
    }

    public void ResetPoints(string rosterId)
    {
        if (string.IsNullOrWhiteSpace(rosterId)) return;
        var changed = false;
        var count = 0;

        lock (_gate)
        {
            var roster = _rosters.FirstOrDefault(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal));
            if (roster is null) return;
            foreach (var student in roster.Students)
            {
                if (student.Points == 0) continue;
                student.Points = 0;
                changed = true;
                count++;
            }
            if (!changed) return;
            roster.UpdatedAt = _now();
            SaveCore();
        }

        Log.Info($"花名册积分清零：{rosterId}，{count} 名学生");
        RaiseChanged();
    }

    public IReadOnlyList<StudentPoints> TopPoints(string rosterId, int count = 10)
    {
        if (string.IsNullOrWhiteSpace(rosterId) || count <= 0) return Array.Empty<StudentPoints>();

        lock (_gate)
        {
            var roster = _rosters.FirstOrDefault(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal));
            if (roster is null) return Array.Empty<StudentPoints>();

            var zh = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
            return roster.Students
                .Where(s => s.IsValid)
                .OrderByDescending(s => s.Points)
                .ThenBy(s => s.HasName ? s.Name.Trim() : s.Id.Trim(), zh)
                .Take(Math.Min(count, 500))
                .Select(s => new StudentPoints
                {
                    RosterId = roster.Id,
                    StudentKey = s.Key,
                    StudentName = s.DisplayName,
                    StudentId = s.DisplayId,
                    Points = s.Points
                })
                .ToList();
        }
    }

    // =========================================================================
    // 导入
    // =========================================================================

    public RosterImportPreview PreviewImport(string filePath)
    {
        var workbook = LoadWorkbook(filePath);
        RosterSheetAnalysis? analysis = null;
        SheetData? sheet = null;
        var bestScore = int.MinValue;

        foreach (var candidate in workbook.Sheets)
        {
            var current = RosterImportAnalyzer.Analyze(candidate);
            // 优先选“识别到姓名/学号列且数据行多”的工作表（跳过说明页、空白页）
            var score = (current.HasRequiredColumns ? 1000 : 0) + Math.Min(current.DataRowCount, 500);
            if (analysis is null || score > bestScore)
            {
                analysis = current;
                sheet = candidate;
                bestScore = score;
            }
        }

        if (analysis is null || sheet is null)
            throw new InvalidOperationException("文件里没有可读取的工作表。");

        var warnings = analysis.Warnings.ToList();
        if (workbook.Sheets.Count > 1)
            warnings.Add($"文件里有 {workbook.Sheets.Count} 个工作表，可在导入窗口中切换。");

        var suggestedName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "花名册";

        return new RosterImportPreview
        {
            FilePath = filePath,
            FormatDescription = DescribeFormat(filePath),
            Sheets = workbook.Sheets.Select(s => s.Name).ToList(),
            SuggestedSheet = sheet.Name,
            SuggestedRosterName = suggestedName,
            SuggestedHeaderRow = analysis.HeaderRow,
            Headers = analysis.Headers,
            SampleRows = analysis.SampleRows,
            EstimatedRows = analysis.DataRowCount,
            SuggestedIdColumn = analysis.IdColumn,
            SuggestedNameColumn = analysis.NameColumn,
            SuggestedScoreColumn = analysis.ScoreColumn,
            Warning = warnings.Count > 0 ? string.Join("；", warnings) : null
        };
    }

    public Roster Import(string filePath, RosterImportOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var workbook = LoadWorkbook(filePath);
        var sheet = workbook.Find(options.SheetName)
                    ?? throw new InvalidOperationException("文件里没有可读取的工作表。");

        if (options.IdColumn is null && options.NameColumn is null)
            throw new InvalidOperationException("请至少为“姓名”或“学号”选择一列，否则无法生成花名册。");

        var headerRow = options.HeaderRow;
        if (headerRow >= sheet.RowCount) headerRow = -1;
        var startRow = headerRow >= 0 ? headerRow + 1 : 0;

        var students = new List<Student>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var r = startRow; r < sheet.RowCount; r++)
        {
            var row = sheet.Rows[r];
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace)) continue;

            var id = ReadCell(row, options.IdColumn);
            var name = ReadCell(row, options.NameColumn);
            if (id.Length == 0 && name.Length == 0) continue; // 姓名和学号都空的行直接忽略

            var key = id.Length > 0 ? "I:" + id : "N:" + name;
            if (!seen.Add(key)) continue; // 去重：同一学号（或同名）只保留第一次出现

            students.Add(new Student
            {
                Id = id,
                Name = name,
                Score = ParseScore(ReadCell(row, options.ScoreColumn))
            });
        }

        if (students.Count == 0)
            throw new InvalidOperationException("没有解析到任何学生，请检查“表头行”和“列映射”是否选对了。");

        var roster = new Roster
        {
            Name = string.IsNullOrWhiteSpace(options.RosterName)
                ? (Path.GetFileNameWithoutExtension(filePath) is { Length: > 0 } n ? n : "导入的花名册")
                : options.RosterName.Trim(),
            SourceFile = filePath,
            SortMode = options.SortMode,
            DisplayModeOverride = options.DisplayModeOverride,
            Students = students
        };
        Add(roster);
        Log.Info($"花名册导入完成：{roster.Name}，{students.Count} 人（{filePath}）");
        return roster;
    }

    private static string ReadCell(IReadOnlyList<string> row, int? column)
    {
        if (column is not { } c || c < 0 || c >= row.Count) return "";
        return (row[c] ?? "").Trim();
    }

    private static double? ParseScore(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().TrimEnd('分', '点').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= 1000)
            return value;
        return null;
    }

    private static string DescribeFormat(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".xlsx" => "Excel 工作簿（.xlsx）",
            ".xlsm" => "Excel 工作簿（.xlsm）",
            ".xltx" => "Excel 模板（.xltx）",
            ".xltm" => "Excel 模板（.xltm）",
            ".csv" => "CSV 文本（逗号分隔）",
            ".tsv" => "TSV 文本（制表符分隔）",
            ".txt" or ".text" => "文本文件（自动识别分隔符）",
            ".xls" => "旧版 Excel 工作簿（.xls）",
            _ => "表格文件"
        };
    }

    private static WorkbookData LoadWorkbook(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("请先选择要导入的花名册文件。");
        if (!File.Exists(filePath))
            throw new InvalidOperationException($"找不到文件：{filePath}");

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".xls")
            throw new InvalidOperationException("暂不支持旧版 .xls（Excel 97-2003），请在 Excel/WPS 中“另存为” .xlsx 后再导入。");

        try
        {
            return SpreadsheetReader.Load(filePath);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"这个文件看起来不是有效的表格：{ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"读取文件失败，文件可能正被其他程序占用：{ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"没有权限读取这个文件：{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法解析这个文件：{ex.Message}", ex);
        }
    }

    // =========================================================================
    // 内部
    // =========================================================================

    private ISettingsService? Settings
    {
        get
        {
            if (_settings is not null) return _settings;
            _settings = TryResolveSettings();
            return _settings;
        }
    }

    private string ActiveRosterId
    {
        get
        {
            var settings = Settings;
            return settings is null ? _fallbackActiveId : settings.Current.ActiveRosterId ?? "";
        }
    }

    private static ISettingsService? TryResolveSettings()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Current.Settings : null;
        }
        catch
        {
            return null;
        }
    }

    private void LoadCore()
    {
        List<Roster> loaded;
        try
        {
            loaded = SafeJsonStore.Load(_filePath, () => new List<Roster>());
        }
        catch (Exception ex)
        {
            Log.Error("读取花名册失败，已从空白开始", ex);
            loaded = new List<Roster>();
        }

        lock (_gate)
        {
            _rosters.Clear();
            foreach (var roster in loaded)
            {
                NormalizeRoster(roster);
                _rosters.Add(roster);
            }
        }
    }

    private void NormalizeRoster(Roster roster)
    {
        if (roster.Students is null) roster.Students = new List<Student>();
        if (string.IsNullOrWhiteSpace(roster.Id)) roster.Id = NewId();
        if (string.IsNullOrWhiteSpace(roster.Name)) roster.Name = "未命名花名册";
        if (roster.CreatedAt == default) roster.CreatedAt = _now();
        if (roster.UpdatedAt == default) roster.UpdatedAt = roster.CreatedAt;
        foreach (var student in roster.Students)
        {
            if (student is null) continue;
            student.Id = student.Id ?? "";
            student.Name = student.Name ?? "";
        }
        roster.Students.RemoveAll(s => s is null || !s.IsValid);
    }

    private void SaveCore() => SafeJsonStore.Save(_filePath, _rosters);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("花名册 Changed 事件处理失败", ex);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
