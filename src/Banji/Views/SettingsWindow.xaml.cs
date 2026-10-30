using System.Windows;

namespace Banji.Views;

public partial class SettingsWindow : Window
{
    /// <summary>可选：打开时定位到的分区（general/buttons/bubble/pet/clock/roster/timetable）。</summary>
    public string? InitialSection { get; set; }

    public SettingsWindow()
    {
        InitializeComponent();
    }
}
