using System.Windows;

namespace Banji.Views;

public partial class TimetableImportWindow : Window
{
    /// <summary>可选：打开时直接加载的文件路径。</summary>
    public string? InitialFilePath { get; set; }

    public TimetableImportWindow()
    {
        InitializeComponent();
    }
}
