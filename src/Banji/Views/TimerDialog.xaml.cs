using System.Windows;
using Banji.Core.Models;

namespace Banji.Views;

public partial class TimerDialog : Window
{
    /// <summary>打开时默认选中的计时类型。</summary>
    public TimerKind InitialKind { get; set; } = TimerKind.Countdown;

    public TimerDialog()
    {
        InitializeComponent();
    }
}
