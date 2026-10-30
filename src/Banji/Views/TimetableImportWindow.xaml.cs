using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Timetable;
using Microsoft.Win32;

namespace Banji.Views;

/// <summary>
/// 课表导入窗口：选择文件 / 工作表 → 自动识别预览（表格 + 识别说明 + warnings）→ 确认导入。
/// 保留 <see cref="InitialFilePath"/> 属性契约（AppServices.ShowTimetableImport 会先设置再 Show）。
/// </summary>
public partial class TimetableImportWindow : Window
{
    private static readonly Brush TextPrimary = MakeBrush("#1F2329");
    private static readonly Brush TextSecondary = MakeBrush("#6B7280");
    private static readonly Brush TextTertiary = MakeBrush("#9AA3AF");
    private static readonly Brush Divider = MakeBrush("#E5E9F2");
    private static readonly Brush Success = MakeBrush("#22A06B");
    private static readonly Brush Warning = MakeBrush("#E07B00");
    private static readonly Brush Error = MakeBrush("#FF3030");

    private string? _initialFilePath;
    private TimetableImportPreview? _preview;
    private bool _loadingSheets;

    /// <summary>可选：打开时直接加载的文件路径（AppServices / QA 使用）。</summary>
    public string? InitialFilePath
    {
        get => _initialFilePath;
        set
        {
            _initialFilePath = value;
            if (!string.IsNullOrWhiteSpace(value) && IsLoaded && IsVisible)
            {
                FilePathBox.Text = value;
                LoadPreview(value);
            }
        }
    }

    public TimetableImportWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AllowDrop = true;
        Drop += OnFileDrop;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            FilePathBox.Text = _initialFilePath;
            LoadPreview(_initialFilePath!);
        }
        else
        {
            SetStatus("请选择课表文件（.xlsx / .xlsm / .csv）。", false);
        }
    }

    // =========================================================================
    // 选择 / 解析
    // =========================================================================

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择课表文件",
            Filter = "课表文件|*.xlsx;*.xlsm;*.csv;*.tsv;*.txt|Excel 工作簿|*.xlsx;*.xlsm|CSV / 文本|*.csv;*.tsv;*.txt|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            FilePathBox.Text = dialog.FileName;
            LoadPreview(dialog.FileName);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => LoadPreview((FilePathBox.Text ?? "").Trim());

    private void OnFilePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoadPreview((FilePathBox.Text ?? "").Trim());
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            FilePathBox.Text = files[0];
            LoadPreview(files[0]);
        }
        catch (Exception ex)
        {
            Log.Error("拖拽导入课表失败", ex);
        }
    }

    private void LoadPreview(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("请先选择课表文件。", true);
            return;
        }
        if (!File.Exists(path))
        {
            SetStatus("文件不存在，请重新选择。", true);
            return;
        }

        try
        {
            SetStatus("正在解析课表…", false);
            var preview = AppServices.Current.Timetable.PreviewImport(path);
            ApplyPreview(preview, path);
        }
        catch (Exception ex)
        {
            Log.Error("解析课表失败", ex);
            _preview = null;
            ClearPreview();
            SetStatus($"解析失败：{ex.Message}", true);
        }
    }

    private void ApplyPreview(TimetableImportPreview preview, string path)
    {
        _preview = preview;
        FilePathBox.Text = path;

        // 工作表列表（避免选择过程中反复触发）
        _loadingSheets = true;
        try
        {
            SheetCombo.Items.Clear();
            foreach (var sheet in preview.Sheets)
                SheetCombo.Items.Add(sheet);
            if (preview.SuggestedSheet is not null && preview.Sheets.Contains(preview.SuggestedSheet))
                SheetCombo.SelectedItem = preview.SuggestedSheet;
            else if (preview.Sheets.Count > 0)
                SheetCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingSheets = false;
        }

        FormatText.Text = string.IsNullOrWhiteSpace(preview.FormatDescription)
            ? "未能识别课表布局。"
            : preview.FormatDescription;

        WarningsHost.Children.Clear();
        if (preview.Warnings.Count == 0)
        {
            WarningsHost.Children.Add(BuildWarningLine("✓ 未发现明显问题，可直接确认导入。", Success));
        }
        else
        {
            foreach (var warning in preview.Warnings)
                WarningsHost.Children.Add(BuildWarningLine("⚠ " + warning, Warning));
        }

        if (preview.Candidate is null)
        {
            ClearPreview();
            BtnConfirm.IsEnabled = false;
            EmptyHint.Text = "未能识别出课表\n\n请检查文件内容，或换一个工作表再试。\n支持：星期为列 / 星期为行 / 每行一节 / 时间表头 等常见布局。";
            SetStatus("识别失败：没有从该工作表读取到课程。", true);
            return;
        }

        BuildPreviewGrid(preview.Candidate);
        BtnConfirm.IsEnabled = true;
        EmptyHint.Visibility = Visibility.Collapsed;
        SetStatus($"识别成功：{preview.Candidate.Periods.Count} 节课、{preview.Candidate.Days.Count} 天课程。确认无误后点击“确认导入”。", false, success: true);
    }

    private void OnSheetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSheets) return;
        if (SheetCombo.SelectedItem is not string sheetName) return;
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || !File.Exists(FilePathBox.Text)) return;

        try
        {
            var preview = TimetableImporter.Preview(FilePathBox.Text, sheetName);
            ApplyPreview(preview, FilePathBox.Text);
        }
        catch (Exception ex)
        {
            Log.Error($"解析课表工作表 {sheetName} 失败", ex);
            SetStatus($"解析工作表“{sheetName}”失败：{ex.Message}", true);
        }
    }

    // =========================================================================
    // 预览表格
    // =========================================================================

    private void BuildPreviewGrid(WeekTimetable timetable)
    {
        PreviewHost.Children.Clear();
        PreviewHost.RowDefinitions.Clear();
        PreviewHost.ColumnDefinitions.Clear();
        EmptyHint.Visibility = Visibility.Collapsed;

        var days = timetable.Days.Keys.OrderBy(WeekdayOrder).ToList();

        PreviewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        foreach (var _ in days)
            PreviewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 88 });

        PreviewHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < timetable.Periods.Count; i++)
            PreviewHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddPreviewCell(0, 0, "节次 / 时间", true, null);
        for (var i = 0; i < days.Count; i++)
            AddPreviewCell(0, i + 1, DayName(days[i]), true, null);

        for (var row = 0; row < timetable.Periods.Count; row++)
        {
            var period = timetable.Periods[row];
            var label = new StackPanel();
            label.Children.Add(new TextBlock
            {
                Text = period.Name,
                FontFamily = (FontFamily)FindResource("Font.Medium"),
                FontSize = 12,
                Foreground = TextPrimary
            });
            label.Children.Add(new TextBlock
            {
                Text = period.TimeText,
                FontSize = 10.5,
                Foreground = TextTertiary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var labelBorder = new Border
            {
                Background = MakeBrush("#FBFCFE"),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2),
                Padding = new Thickness(8, 5, 8, 5),
                Child = label
            };
            Grid.SetRow(labelBorder, row + 1);
            Grid.SetColumn(labelBorder, 0);
            PreviewHost.Children.Add(labelBorder);

            for (var i = 0; i < days.Count; i++)
            {
                var course = timetable.CourseAt(days[i], period.Index) ?? "";
                AddPreviewCell(row + 1, i + 1, string.IsNullOrWhiteSpace(course) ? "—" : course, false,
                    string.IsNullOrWhiteSpace(course) ? TextTertiary : TextPrimary);
            }
        }
    }

    private void AddPreviewCell(int row, int column, string text, bool header, Brush? foreground)
    {
        var border = new Border
        {
            Background = header ? MakeBrush("#EFF3FA") : Brushes.White,
            BorderBrush = header ? Brushes.Transparent : Divider,
            BorderThickness = new Thickness(header ? 0 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
            Padding = new Thickness(8, 6, 8, 6),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource(header ? "Font.Medium" : "Font.Regular"),
                FontSize = header ? 12 : 13,
                Foreground = foreground ?? TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        PreviewHost.Children.Add(border);
    }

    private void ClearPreview()
    {
        PreviewHost.Children.Clear();
        PreviewHost.RowDefinitions.Clear();
        PreviewHost.ColumnDefinitions.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(EmptyHint.Text))
            EmptyHint.Text = "未能识别出课表，请检查文件内容。";
    }

    private TextBlock BuildWarningLine(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };

    // =========================================================================
    // 确认导入 / 取消
    // =========================================================================

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_preview?.Candidate is null)
        {
            SetStatus("还没有可导入的课表，请先选择并解析文件。", true);
            return;
        }

        var path = (FilePathBox.Text ?? "").Trim();
        var sheet = SheetCombo.SelectedItem as string;
        try
        {
            var timetable = AppServices.Current.Timetable.Import(path, sheet);
            SetStatus($"导入成功：{timetable.Periods.Count} 节课、{timetable.Days.Count} 天课程已保存，可在“课堂时段设置”中微调时间。", false, success: true);
            Log.Info($"课表导入成功：{Path.GetFileName(path)} / {sheet}，{timetable.Periods.Count} 节，{timetable.Days.Count} 天");
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("导入课表失败", ex);
            SetStatus($"导入失败：{ex.Message}", true);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // 辅助
    // =========================================================================

    private void SetStatus(string message, bool isError, bool success = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? Error : success ? Success : TextSecondary;
    }

    private static int WeekdayOrder(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        _ => 6
    };

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

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
