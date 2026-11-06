using Banji.Core.Models;

namespace Banji.Core.Settings;

/// <summary>应用全部可自定义项。所有默认值来自开发文档，均可在设置里改。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // ---------------- 抽签 ----------------
    public string ActiveRosterId { get; set; } = "";
    public PickDisplayMode PickDisplay { get; set; } = PickDisplayMode.Both;
    /// <summary>标准课时长度（分钟），抽签约束窗口默认用它；与课堂时段绑定。</summary>
    public int ClassDurationMinutes { get; set; } = 45;
    /// <summary>若当天课表可用，抽签窗口优先使用“当前这节课”的真实时长（精确到秒）。</summary>
    public bool PickWindowFollowsCurrentPeriod { get; set; } = true;
    /// <summary>每节课开始时清空“本节课”抽签计数（滚动窗口计数仍保留）。</summary>
    public bool ResetPickSessionOnClassStart { get; set; } = true;
    public bool PickShowIdWhenPickingName { get; set; } = true;

    // ---------------- 课表 / 时钟 ----------------
    /// <summary>铃声误差（秒），[-60, 60]，默认 0。正值表示本程序比铃声慢多少秒（界面上会解释）。</summary>
    public int BellOffsetSeconds { get; set; } = 0;
    public bool ShowClassStartNotice { get; set; } = true;
    public bool ShowClassEndNotice { get; set; } = true;
    /// <summary>上课/下课全屏提示停留秒数。</summary>
    public int NoticeDurationSeconds { get; set; } = 4;
    /// <summary>下课后“下课超时”在宠物上停留的秒数。</summary>
    public int EndOverrunSeconds { get; set; } = 15;
    /// <summary>上课提示背景预设：blue / gradient / orange / custom（自定义取 StartNoticeBackground）。</summary>
    public string StartNoticePreset { get; set; } = "gradient";
    public string StartNoticeBackground { get; set; } = "#317FFF";
    public bool StartNoticeBlurBackdrop { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = false;

    // ---------------- 宠物 ----------------
    /// <summary>宠物整体缩放，无级、无上下限（UI 允许 0.05~20，滑块 0.3~3）。</summary>
    public double UiScale { get; set; } = 1.0;
    /// <summary>宠物距屏幕右边缘的偏移（px，缩放前基准单位）。</summary>
    public double PetRightMargin { get; set; } = 0;
    /// <summary>宠物距工作区底部的偏移（px）。</summary>
    public double PetBottomMargin { get; set; } = 0;
    public bool TimetableExpanded { get; set; } = true;
    public bool ShowTimetablePanel { get; set; } = true;
    /// <summary>宠物常驻信息里显示考试/重要日期倒计时（D-Day）。</summary>
    public bool ShowCountdownOnPet { get; set; } = true;
    /// <summary>考试/重要日期倒计时事件列表。</summary>
    public List<CountdownEventItem> CountdownEvents { get; set; } = new();
    public bool EnableQuips { get; set; } = true;
    /// <summary>使用本地台词生成器（模板+语料组合，零联网），比固定台词库更不容易重复。</summary>
    public bool UseLocalQuipGenerator { get; set; } = true;
    /// <summary>宠物陪伴养成统计（使用天数/摸头/抽签/成就）。</summary>
    public bool EnableGrowth { get; set; } = true;
    public bool PetBlinkEnabled { get; set; } = true;
    public bool PetBobEnabled { get; set; } = true;
    /// <summary>“清醒一下”播放的音量（0~1），默认 1 = 应用内最大音量。</summary>
    public double StingVolume { get; set; } = 1.0;

    public PetStyleSettings Pet { get; set; } = new();
    public ButtonStyleSettings Buttons { get; set; } = new();
    public BubbleStyleSettings Bubble { get; set; } = new();
    public NoticeStyleSettings Notice { get; set; } = new();

    /// <summary>把非法值收敛到合法范围，并补齐嵌套对象。</summary>
    public void Normalize()
    {
        Pet ??= new PetStyleSettings();
        Buttons ??= new ButtonStyleSettings();
        Bubble ??= new BubbleStyleSettings();
        Notice ??= new NoticeStyleSettings();
        CountdownEvents ??= new List<CountdownEventItem>();
        CountdownEvents.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Name));
        foreach (var ev in CountdownEvents)
        {
            if (!ev.Color.StartsWith('#')) ev.Color = "#" + ev.Color;
        }

        if (UiScale <= 0) UiScale = 1.0;
        UiScale = Math.Clamp(UiScale, 0.05, 20.0);
        ClassDurationMinutes = Math.Clamp(ClassDurationMinutes, 1, 600);
        BellOffsetSeconds = Math.Clamp(BellOffsetSeconds, -60, 60);
        NoticeDurationSeconds = Math.Clamp(NoticeDurationSeconds, 1, 60);
        EndOverrunSeconds = Math.Clamp(EndOverrunSeconds, 3, 120);
        StingVolume = Math.Clamp(StingVolume, 0.0, 1.0);
        PetRightMargin = Math.Clamp(PetRightMargin, 0, 4000);
        PetBottomMargin = Math.Clamp(PetBottomMargin, 0, 4000);

        Buttons.Normalize();
        Bubble.Normalize();
        Notice.Normalize();
        Pet.Normalize();
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Pet = Pet.Clone();
        copy.Buttons = Buttons.Clone();
        copy.Bubble = Bubble.Clone();
        copy.Notice = Notice.Clone();
        copy.CountdownEvents = CountdownEvents.Select(c => c.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// 宠物外观（第二轮用户要求）：<b>方形、扁扁的</b>小方块生物，两只小方形手 + 两只小方形脚，
/// 两只纯黑实心小圆点眼睛；无腮红/鼻子/嘴巴/高光；颜色不用橙色，默认雾蓝色。
/// </summary>
public sealed class PetStyleSettings
{
    public string BodyColor { get; set; } = "#8FB6F2";
    /// <summary>身体底边/手臂的略深色。</summary>
    public string BodyShadeColor { get; set; } = "#6E96DA";
    /// <summary>身体正面下半部分的浅色（可选细节，默认同色系）。</summary>
    public string BellyColor { get; set; } = "#B7CFF7";
    public string EyeColor { get; set; } = "#231F20";
    public string CheekColor { get; set; } = "#FFB4A2";
    /// <summary>默认关闭（用户明确要求不要腮红）。</summary>
    public bool ShowCheeks { get; set; } = false;

    public void Normalize()
    {
        BodyColor = NormalizeHex(BodyColor, "#8FB6F2");
        BodyShadeColor = NormalizeHex(BodyShadeColor, "#6E96DA");
        BellyColor = NormalizeHex(BellyColor, "#B7CFF7");
        EyeColor = NormalizeHex(EyeColor, "#231F20");
        CheekColor = NormalizeHex(CheekColor, "#FFB4A2");
    }

    public PetStyleSettings Clone() => (PetStyleSettings)MemberwiseClone();

    internal static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>功能区按钮样式（默认值即开发文档第 4 章附录）。</summary>
public sealed class ButtonStyleSettings
{
    public string IdleColor { get; set; } = "#317FFF";
    public int IdleOpacityPercent { get; set; } = 10;
    public string HoverColor { get; set; } = "#5998FF";
    public int HoverOpacityPercent { get; set; } = 0;
    public string PressedColor { get; set; } = "#0051D6";
    public int PressedOpacityPercent { get; set; } = 0;
    public string AccentColor { get; set; } = "#FF3030";
    public int AccentOpacityPercent { get; set; } = 0;
    public double BorderThickness { get; set; } = 1.5;
    /// <summary>圆角 R，默认 8，与 Win11 输入法预览框一致。</summary>
    public double CornerRadius { get; set; } = 8;
    /// <summary>阴影右下 45°，距离 3 磅 ≈ 4px。</summary>
    public double ShadowOffset { get; set; } = 4;
    public int ShadowOpacityPercent { get; set; } = 12;
    public string ShadowColor { get; set; } = "#000000";
    public double FontSize { get; set; } = 14;

    public void Normalize()
    {
        IdleColor = PetStyleSettings.NormalizeHex(IdleColor, "#317FFF");
        HoverColor = PetStyleSettings.NormalizeHex(HoverColor, "#5998FF");
        PressedColor = PetStyleSettings.NormalizeHex(PressedColor, "#0051D6");
        AccentColor = PetStyleSettings.NormalizeHex(AccentColor, "#FF3030");
        ShadowColor = PetStyleSettings.NormalizeHex(ShadowColor, "#000000");
        IdleOpacityPercent = Math.Clamp(IdleOpacityPercent, 0, 100);
        HoverOpacityPercent = Math.Clamp(HoverOpacityPercent, 0, 100);
        PressedOpacityPercent = Math.Clamp(PressedOpacityPercent, 0, 100);
        AccentOpacityPercent = Math.Clamp(AccentOpacityPercent, 0, 100);
        BorderThickness = Math.Clamp(BorderThickness, 0, 8);
        CornerRadius = Math.Clamp(CornerRadius, 0, 40);
        ShadowOffset = Math.Clamp(ShadowOffset, 0, 24);
        ShadowOpacityPercent = Math.Clamp(ShadowOpacityPercent, 0, 100);
        FontSize = Math.Clamp(FontSize, 8, 40);
    }

    public ButtonStyleSettings Clone() => (ButtonStyleSettings)MemberwiseClone();
}

/// <summary>对话气泡外观（第二轮用户要求：白底、不使用高斯模糊底）。</summary>
public sealed class BubbleStyleSettings
{
    /// <summary>“透明度”语义：0 = 完全不透明的白底（默认），50 = 半透明。</summary>
    public int OpacityPercent { get; set; } = 0;
    /// <summary>是否使用截屏高斯模糊作为气泡底（默认关闭；白底更清楚）。</summary>
    public bool UseBlur { get; set; } = false;
    public double BlurRadius { get; set; } = 14;
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string BorderColor { get; set; } = "#3E6BFF";
    public double BorderThickness { get; set; } = 2;
    public string TextColor { get; set; } = "#2E69FF";
    public double MaxWidth { get; set; } = 380;
    /// <summary>打字机速度：每个字的毫秒数。</summary>
    public int TypewriterMsPerChar { get; set; } = 28;
    /// <summary>抽签结果气泡停留毫秒数（文档要求 10 秒）。</summary>
    public int PickBubbleDurationMs { get; set; } = 10000;
    public bool ShowTail { get; set; } = true;

    public void Normalize()
    {
        OpacityPercent = Math.Clamp(OpacityPercent, 0, 100);
        BlurRadius = Math.Clamp(BlurRadius, 0, 60);
        BorderColor = PetStyleSettings.NormalizeHex(BorderColor, "#3E6BFF");
        BackgroundColor = PetStyleSettings.NormalizeHex(BackgroundColor, "#FFFFFF");
        TextColor = PetStyleSettings.NormalizeHex(TextColor, "#2E69FF");
        BorderThickness = Math.Clamp(BorderThickness, 0, 10);
        MaxWidth = Math.Clamp(MaxWidth, 120, 1200);
        TypewriterMsPerChar = Math.Clamp(TypewriterMsPerChar, 0, 200);
        PickBubbleDurationMs = Math.Clamp(PickBubbleDurationMs, 1000, 60000);
    }

    public BubbleStyleSettings Clone() => (BubbleStyleSettings)MemberwiseClone();
}

/// <summary>全屏上下课提示样式。</summary>
public sealed class NoticeStyleSettings
{
    public string ClassStartTextColor { get; set; } = "#FFFFFF";
    /// <summary>下课纯文字颜色：默认白色，配合自动对比描边，在任何桌面/PPT 背景上都能看清。</summary>
    public string ClassEndTextColor { get; set; } = "#FFFFFF";
    public int ClassEndTextOpacityPercent { get; set; } = 92;
    public double ClassStartFontSize { get; set; } = 130;
    public double ClassEndFontSize { get; set; } = 110;
    public double TimerTimeoutFontSize { get; set; } = 96;
    public int BlurBackdropOpacityPercent { get; set; } = 45;

    public void Normalize()
    {
        ClassStartTextColor = PetStyleSettings.NormalizeHex(ClassStartTextColor, "#FFFFFF");
        ClassEndTextColor = PetStyleSettings.NormalizeHex(ClassEndTextColor, "#1F2329");
        ClassEndTextOpacityPercent = Math.Clamp(ClassEndTextOpacityPercent, 0, 100);
        ClassStartFontSize = Math.Clamp(ClassStartFontSize, 40, 300);
        ClassEndFontSize = Math.Clamp(ClassEndFontSize, 40, 300);
        TimerTimeoutFontSize = Math.Clamp(TimerTimeoutFontSize, 40, 300);
        BlurBackdropOpacityPercent = Math.Clamp(BlurBackdropOpacityPercent, 0, 100);
    }

    public NoticeStyleSettings Clone() => (NoticeStyleSettings)MemberwiseClone();
}
