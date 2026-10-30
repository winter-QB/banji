using System.Windows;
using System.Windows.Controls;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 手动录入花名册：逐行敲学号/姓名/成绩，或整批粘贴姓名。
/// 姓名与学号至少填写一个（都空的行在保存时被忽略）。
/// </summary>
public partial class ManualRosterDialog : Window
{
    private readonly ObservableCollection<Student> _students = new();

    public ManualRosterDialog()
    {
        InitializeComponent();
        StudentGrid.ItemsSource = _students;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_students.Count == 0) AddRow();
        NameBox.Text = "新花名册";
        NameBox.SelectAll();
        NameBox.Focus();
        UpdateRowCount();
    }

    // =========================================================================
    // 行编辑
    // =========================================================================

    private void AddRow()
    {
        _students.Add(new Student());
        UpdateRowCount();
    }

    private void OnAddRowClick(object sender, RoutedEventArgs e)
    {
        AddRow();
        StudentGrid.SelectedIndex = _students.Count - 1;
        StudentGrid.ScrollIntoView(_students[^1]);
        StudentGrid.Focus();
    }

    private void OnRemoveRowClick(object sender, RoutedEventArgs e)
    {
        StudentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (StudentGrid.SelectedItem is Student selected)
        {
            _students.Remove(selected);
            if (_students.Count == 0) AddRow();
            UpdateRowCount();
            return;
        }
        if (_students.Count > 0)
        {
            _students.RemoveAt(_students.Count - 1);
            if (_students.Count == 0) AddRow();
            UpdateRowCount();
            return;
        }
        StatusText.Text = "请先在表格里选中要删除的行。";
    }

    private void OnClearRowsClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this, "确定要清空当前所有学生行吗？", "清空",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        _students.Clear();
        AddRow();
        StatusText.Text = "已清空所有行";
    }

    private void UpdateRowCount() => RowCountText.Text = $"{_students.Count(s => s.IsValid)} 人 / {_students.Count} 行";

    // =========================================================================
    // 批量粘贴
    // =========================================================================

    private void OnParsePasteClick(object sender, RoutedEventArgs e)
    {
        var text = PasteBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "粘贴框是空的，请先粘贴一些姓名再解析。";
            return;
        }

        // 先移除全空的占位行，避免解析后表格里一堆空行
        for (var i = _students.Count - 1; i >= 0; i--)
        {
            if (!_students[i].IsValid) _students.RemoveAt(i);
        }

        var added = 0;
        var skipped = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = SplitLine(line);
            string id = "", name = "", scoreText = "";
            if (parts.Length == 1)
            {
                if (LooksLikeStudentId(parts[0])) id = parts[0];
                else name = parts[0];
            }
            else if (parts.Length == 2)
            {
                if (LooksLikeStudentId(parts[0]) && !LooksLikeStudentId(parts[1]))
                {
                    id = parts[0];
                    name = parts[1];
                }
                else if (!LooksLikeStudentId(parts[0]) && LooksLikeStudentId(parts[1]))
                {
                    name = parts[0];
                    id = parts[1];
                }
                else
                {
                    name = parts[0];
                    scoreText = parts[1];
                }
            }
            else
            {
                if (LooksLikeStudentId(parts[0]))
                {
                    id = parts[0];
                    name = parts[1];
                    scoreText = parts[2];
                }
                else
                {
                    name = parts[0];
                    id = parts[1];
                    scoreText = parts[2];
                }
            }

            var student = new Student { Id = id.Trim(), Name = name.Trim(), Score = ParseScore(scoreText) };
            if (!student.IsValid)
            {
                skipped++;
                continue;
            }
            if (_students.Any(s => s.IsValid && string.Equals(s.Key, student.Key, StringComparison.Ordinal)))
            {
                skipped++;
                continue;
            }
            _students.Add(student);
            added++;
        }

        if (_students.Count == 0) AddRow();
        PasteBox.Clear();
        UpdateRowCount();
        StatusText.Text = added > 0
            ? $"已从粘贴内容添加 {added} 名学生" + (skipped > 0 ? $"，跳过 {skipped} 个重复或无效项" : "")
            : "没有从粘贴内容里识别出学生，请检查格式（一行一个姓名即可）。";
    }

    private static string[] SplitLine(string line)
    {
        if (line.Contains('\t') || line.Contains(',') || line.Contains('，') || line.Contains(';') || line.Contains('；'))
        {
            return line
                .Split(new[] { '\t', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
        }
        return line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
    }

    private static bool LooksLikeStudentId(string text)
        => text.Length >= 4 && text.All(char.IsDigit);

    private static double? ParseScore(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().TrimEnd('分', '点').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= 1000)
            return value;
        return null;
    }

    // =========================================================================
    // 保存 / 取消
    // =========================================================================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        StudentGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var valid = new List<Student>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var student in _students)
        {
            if (student is null) continue;
            student.Id = (student.Id ?? "").Trim();
            student.Name = (student.Name ?? "").Trim();
            if (!student.IsValid || !seen.Add(student.Key))
            {
                dropped++;
                continue;
            }
            valid.Add(student);
        }

        if (valid.Count == 0)
        {
            StatusText.Text = "请至少填写一条有效记录：姓名和学号至少填一个。";
            MessageBox.Show(this, "请至少填写一位学生的姓名或学号。", "还差一点",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名花名册" : NameBox.Text.Trim();
            var subject = string.IsNullOrWhiteSpace(SubjectBox.Text) ? null : SubjectBox.Text.Trim();
            var roster = AppServices.Current.Rosters.CreateManual(name, valid, subject);
            AppServices.Current.Rosters.SetActive(roster.Id);
            Log.Info($"手动创建花名册：{roster.Name}，{valid.Count} 人");
            StatusText.Text = $"已保存“{roster.Name}”";
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("手动保存花名册失败", ex);
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
