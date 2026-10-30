using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Rosters;
using Microsoft.Win32;

namespace Banji.Views;

/// <summary>
/// 花名册导入窗口：选文件 → 自动识别 sheet / 表头行 / 列映射 → 预览样例 → 导入。
/// 支持 .xlsx/.xlsm/.csv/.tsv/.txt；旧版 .xls 给出友好中文提示。
/// </summary>
public partial class RosterImportWindow : Window
{
    /// <summary>可选：打开时直接加载的文件路径（AppServices 会在 Show 之后设置，故用 setter 延迟加载）。</summary>
    public string? InitialFilePath
    {
        get => _initialFilePath;
        set
        {
            _initialFilePath = value;
            if (_loaded && !string.IsNullOrWhiteSpace(value) && File.Exists(value))
                LoadFile(value);
        }
    }

    private string? _initialFilePath;
    private WorkbookData? _workbook;
    private SheetData? _sheet;
    private bool _suppressEvents;
    private bool _loaded;

    public RosterImportWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_initialFilePath) && File.Exists(_initialFilePath))
        {
            LoadFile(_initialFilePath!);
        }
        UpdateHint();
    }

    // =========================================================================
    // 文件加载
    // =========================================================================

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择花名册文件",
            CheckFileExists = true,
            Filter =
                "表格文件 (*.xlsx;*.xlsm;*.csv;*.tsv;*.txt)|*.xlsx;*.xlsm;*.csv;*.tsv;*.txt|" +
                "Excel 工作簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|" +
                "CSV / TSV / TXT (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|" +
                "旧版 Excel (*.xls)|*.xls|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
    }

    private void LoadFile(string path)
    {
        ClearError();
        try
        {
            _workbook = SpreadsheetReader.Load(path);
        }
        catch (NotSupportedException ex)
        {
            ShowError(ex.Message);
            return;
        }
        catch (InvalidDataException ex)
        {
            ShowError($"这个文件看起来不是有效的表格：{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"加载花名册文件失败：{path}", ex);
            ShowError($"无法读取这个文件：{ex.Message}");
            return;
        }

        if (_workbook.Sheets.Count == 0)
        {
            ShowError("文件里没有可读取的工作表。");
            return;
        }

        FilePathBox.Text = path;
        FilePathBox.ToolTip = path;
        NameBox.Text = Path.GetFileNameWithoutExtension(path);

        _suppressEvents = true;
        try
        {
            SheetCombo.Items.Clear();
            foreach (var sheet in _workbook.Sheets) SheetCombo.Items.Add(sheet.Name);

            // 默认选“识别结果最好 + 数据最多”的工作表
            var bestIndex = 0;
            var bestScore = int.MinValue;
            for (var i = 0; i < _workbook.Sheets.Count; i++)
            {
                var analysis = RosterImportAnalyzer.Analyze(_workbook.Sheets[i]);
                var score = (analysis.HasRequiredColumns ? 1000 : 0) + Math.Min(analysis.DataRowCount, 500);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            SheetCombo.SelectedIndex = bestIndex;
        }
        finally
        {
            _suppressEvents = false;
        }

        LoadSelectedSheet();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _workbook = null;
        _sheet = null;
        FilePathBox.Text = "";
        FilePathBox.ToolTip = null;
        NameBox.Text = "";
        _suppressEvents = true;
        try
        {
            SheetCombo.Items.Clear();
            HeaderCombo.Items.Clear();
            IdColumnCombo.Items.Clear();
            NameColumnCombo.Items.Clear();
            ScoreColumnCombo.Items.Clear();
        }
        finally
        {
            _suppressEvents = false;
        }
        SampleGrid.Columns.Clear();
        SampleGrid.ItemsSource = null;
        HintText.Text = "选择一个文件后，这里会显示自动识别的结果。";
        ClearError();
    }

    // =========================================================================
    // 工作表 / 表头 / 列映射
    // =========================================================================

    private void OnSheetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        LoadSelectedSheet();
    }

    private void LoadSelectedSheet()
    {
        if (_workbook is null) return;
        var index = SheetCombo.SelectedIndex;
        if (index < 0 || index >= _workbook.Sheets.Count) return;

        _sheet = _workbook.Sheets[index];
        var analysis = RosterImportAnalyzer.Analyze(_sheet);

        _suppressEvents = true;
        try
        {
            // 表头行下拉：第 1 项为“无表头”，后面是前 12 行原始内容
            HeaderCombo.Items.Clear();
            HeaderCombo.Items.Add("无表头（第一行就是学生）");
            var rowLimit = Math.Min(_sheet.RowCount, 12);
            for (var r = 0; r < rowLimit; r++)
                HeaderCombo.Items.Add(RosterImportAnalyzer.DescribeRow(_sheet, r));
            HeaderCombo.SelectedIndex = analysis.HeaderRow >= 0
                ? Math.Min(analysis.HeaderRow + 1, HeaderCombo.Items.Count - 1)
                : 0;

            var labels = RosterImportAnalyzer.BuildColumnLabels(_sheet, analysis.HeaderRow);
            FillColumnCombo(IdColumnCombo, labels, analysis.IdColumn);
            FillColumnCombo(NameColumnCombo, labels, analysis.NameColumn);
            FillColumnCombo(ScoreColumnCombo, labels, analysis.ScoreColumn);
        }
        finally
        {
            _suppressEvents = false;
        }

        RefreshSampleGrid();
        UpdateHint();
    }

    private void OnHeaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _sheet is null) return;

        var headerRow = GetSelectedHeaderRow();
        var idColumn = ReadColumnIndex(IdColumnCombo);
        var nameColumn = ReadColumnIndex(NameColumnCombo);
        var scoreColumn = ReadColumnIndex(ScoreColumnCombo);

        var labels = RosterImportAnalyzer.BuildColumnLabels(_sheet, headerRow);
        _suppressEvents = true;
        try
        {
            FillColumnCombo(IdColumnCombo, labels, idColumn);
            FillColumnCombo(NameColumnCombo, labels, nameColumn);
            FillColumnCombo(ScoreColumnCombo, labels, scoreColumn);
        }
        finally
        {
            _suppressEvents = false;
        }

        RefreshSampleGrid();
        UpdateHint();
    }

    private void OnColumnMappingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _sheet is null) return;
        RefreshSampleGrid();
        UpdateHint();
    }

    private static void FillColumnCombo(ComboBox combo, IReadOnlyList<string> labels, int? selectedColumn)
    {
        combo.Items.Clear();
        combo.Items.Add("（不导入）");
        foreach (var label in labels) combo.Items.Add(label);
        combo.SelectedIndex = selectedColumn is { } column && column >= 0 && column < labels.Count
            ? column + 1
            : 0;
    }

    private static int? ReadColumnIndex(ComboBox combo)
        => combo.SelectedIndex > 0 ? combo.SelectedIndex - 1 : null;

    private int GetSelectedHeaderRow() => HeaderCombo.SelectedIndex - 1;

    // =========================================================================
    // 样例表格
    // =========================================================================

    private void RefreshSampleGrid()
    {
        if (_sheet is null) return;

        var headerRow = GetSelectedHeaderRow();
        var rows = RosterImportAnalyzer.BuildPreviewRows(_sheet, headerRow, RosterImportAnalyzer.MaxSampleRows);
        var columnCount = Math.Max(_sheet.Rows.Count == 0 ? 0 : _sheet.Rows.Max(r => r.Count), 1);
        var labels = RosterImportAnalyzer.BuildColumnLabels(_sheet, headerRow);
        var idColumn = ReadColumnIndex(IdColumnCombo);
        var nameColumn = ReadColumnIndex(NameColumnCombo);
        var scoreColumn = ReadColumnIndex(ScoreColumnCombo);

        var table = new DataTable();
        for (var c = 0; c < columnCount; c++) table.Columns.Add("C" + c, typeof(string));
        foreach (var row in rows)
        {
            var values = new object[columnCount];
            for (var c = 0; c < columnCount; c++) values[c] = c < row.Count ? row[c] : "";
            table.Rows.Add(values);
        }

        SampleGrid.Columns.Clear();
        for (var c = 0; c < columnCount; c++)
        {
            var header = labels[c];
            if (c == idColumn) header = "★ 学号";
            else if (c == nameColumn) header = "★ 姓名";
            else if (c == scoreColumn) header = "★ 成绩";
            if (header.Length > 20) header = header[..20] + "…";

            SampleGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"[C{c}]"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }
        SampleGrid.ItemsSource = table.DefaultView;
    }

    // =========================================================================
    // 提示 / 错误
    // =========================================================================

    private void UpdateHint()
    {
        if (_sheet is null)
        {
            HintText.Text = "选择一个文件后，这里会显示自动识别的结果。";
            return;
        }

        var idColumn = ReadColumnIndex(IdColumnCombo);
        var nameColumn = ReadColumnIndex(NameColumnCombo);
        var scoreColumn = ReadColumnIndex(ScoreColumnCombo);
        var headerRow = GetSelectedHeaderRow();

        var start = headerRow >= 0 ? headerRow + 1 : 0;
        var dataRowCount = 0;
        for (var r = start; r < _sheet.RowCount; r++)
        {
            if (!_sheet.Rows[r].All(string.IsNullOrWhiteSpace)) dataRowCount++;
        }

        if (idColumn is null && nameColumn is null)
        {
            HintText.Text = "⚠ 还没有选择“姓名”或“学号”列，请至少选择其中一列。";
            return;
        }

        var parts = new List<string>();
        if (nameColumn is { } n) parts.Add($"姓名 = 第 {n + 1} 列");
        if (idColumn is { } i) parts.Add($"学号 = 第 {i + 1} 列");
        if (scoreColumn is { } s) parts.Add($"成绩 = 第 {s + 1} 列");
        HintText.Text = $"已识别：{string.Join("，", parts)}；预计 {dataRowCount} 行数据（空行与重复项自动跳过）。";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = "⚠ " + message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // 导入 / 取消
    // =========================================================================

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        ClearError();

        var path = FilePathBox.Text?.Trim() ?? "";
        if (path.Length == 0)
        {
            ShowError("请先选择一个要导入的花名册文件。");
            return;
        }
        if (!File.Exists(path))
        {
            ShowError("找不到这个文件，请重新选择。");
            return;
        }

        var idColumn = ReadColumnIndex(IdColumnCombo);
        var nameColumn = ReadColumnIndex(NameColumnCombo);
        if (idColumn is null && nameColumn is null)
        {
            ShowError("请至少为“姓名”或“学号”选择一列，否则无法生成花名册。");
            return;
        }

        var options = new RosterImportOptions
        {
            RosterName = string.IsNullOrWhiteSpace(NameBox.Text)
                ? (Path.GetFileNameWithoutExtension(path) is { Length: > 0 } fallback ? fallback : "导入的花名册")
                : NameBox.Text.Trim(),
            SheetName = SheetCombo.SelectedItem as string,
            HeaderRow = GetSelectedHeaderRow(),
            IdColumn = idColumn,
            NameColumn = nameColumn,
            ScoreColumn = ReadColumnIndex(ScoreColumnCombo)
        };

        try
        {
            ImportButton.IsEnabled = false;
            var roster = AppServices.Current.Rosters.Import(path, options);
            try
            {
                AppServices.Current.Rosters.SetActive(roster.Id);
            }
            catch (Exception ex)
            {
                Log.Error("导入后设置当前花名册失败", ex);
            }

            MessageBox.Show(this,
                $"导入成功！\n\n花名册：“{roster.Name}”\n学生人数：{roster.Students.Count} 人\n已自动设为当前抽签花名册。",
                "导入花名册", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("导入花名册失败", ex);
            ShowError(ex.Message);
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
