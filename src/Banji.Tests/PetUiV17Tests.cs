using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.7.0 回归：白卡黑边（Halo）、便签拖动策略。</summary>
public static class PetUiV17Tests
{
    [BanjiTest]
    public static void 白卡与便签都必须关闭Halo()
    {
        Assert.False(TimetablePanelPolicy.ShouldEnableHalo(TimetablePanelSurface.WhiteCard));
        Assert.False(TimetablePanelPolicy.ShouldEnableHalo(TimetablePanelSurface.FocusNote));
        // 旧透明底样式仍保留开关语义，避免策略退化为“永远 false”
        Assert.True(TimetablePanelPolicy.ShouldEnableHalo(TimetablePanelSurface.LegacyTransparent));
    }

    [BanjiTest]
    public static void Halo控件可开关且关闭后隐藏光晕层()
    {
        RunSta(() =>
        {
        var line = new HaloTextLine();
        Assert.True(line.HaloEnabled, "默认应开启光晕");
        line.HaloEnabled = false;
        Assert.False(line.HaloEnabled);
        // HaloTextLine = 1 宽光晕 + 4 描边 + 1 正文；关闭后前 5 层必须 Collapsed，正文字仍可见
        var children = new List<System.Windows.DependencyObject>();
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(line); i++)
        {
            children.Add(System.Windows.Media.VisualTreeHelper.GetChild(line, i));
        }
        Assert.Equal(6, children.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.UIElement)children[i]).Visibility);
        }
        Assert.Equal(System.Windows.Visibility.Visible, ((System.Windows.UIElement)children[5]).Visibility);

        var stack = new HaloTextStack();
        Assert.True(stack.HaloEnabled);
        stack.HaloEnabled = false;
        Assert.False(stack.HaloEnabled);
        });
    }

    /// <summary>WPF 控件必须在 STA 线程创建；测试主线程是 MTA，这里单独起 STA 线程执行。</summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception(error.Message, error);
    }

    [BanjiTest]
    public static void 便签态只有自由位置可拖动()
    {
        Assert.True(TimetablePanelPolicy.CanDragPanel(false, FocusNotePlacement.TopLeftHorizontal, false));
        Assert.True(TimetablePanelPolicy.CanDragPanel(true, FocusNotePlacement.Free, false));
        Assert.False(TimetablePanelPolicy.CanDragPanel(true, FocusNotePlacement.TopLeftHorizontal, false));
        Assert.False(TimetablePanelPolicy.CanDragPanel(true, FocusNotePlacement.BottomRightVertical, false));
        Assert.True(TimetablePanelPolicy.CanDragPanel(true, FocusNotePlacement.TopLeftHorizontal, true));
    }

    [BanjiTest]
    public static void 课表窗永远位于宠物窗下方()
    {
        Assert.True(TimetablePanelPolicy.ShouldPlacePanelBelowPet);
    }
}


