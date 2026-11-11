using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>v1.7.0 课表窗口的表面类型（决定文字是否带 Halo 光晕）。</summary>
public enum TimetablePanelSurface
{
    /// <summary>普通白底卡片：深色文字，必须关闭黑色光晕/描边。</summary>
    WhiteCard,
    /// <summary>专注态便签：独立白色 TextBlock + 黑色投影，不使用 Halo 控件。</summary>
    FocusNote,
    /// <summary>v1.5 旧版“透明底 + 白字光晕”（仅保留策略分支，v1.6 起不再使用）。</summary>
    LegacyTransparent
}

/// <summary>
/// v1.7.0 课表窗口显示策略（纯函数，便于回归测试）：
/// 白卡深色字若仍开着 Halo 会糊成黑边重影（v1.6 拆分时漏切 HaloEnabled 的回归点）。
/// </summary>
public static class TimetablePanelPolicy
{
    /// <summary>当前表面是否应开启 Halo 黑光晕/四向描边。</summary>
    public static bool ShouldEnableHalo(TimetablePanelSurface surface) => surface switch
    {
        TimetablePanelSurface.WhiteCard => false,
        TimetablePanelSurface.FocusNote => false,
        TimetablePanelSurface.LegacyTransparent => true,
        _ => false
    };

    /// <summary>便签态下是否允许拖动整窗：只有 Free 位置（或 QA 覆盖了自由位置）可拖。</summary>
    public static bool CanDragPanel(bool focusActive, FocusNotePlacement placement, bool hasFreeOverride)
        => !focusActive || placement == FocusNotePlacement.Free || hasFreeOverride;

    /// <summary>课表窗口必须始终位于宠物窗口下方（两窗都 Topmost 时重叠点击宠物优先）。</summary>
    public static bool ShouldPlacePanelBelowPet => true;
}
