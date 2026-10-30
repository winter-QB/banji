using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Banji.Core.Boot;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Views;

/// <summary>设置窗口各分区内容（由 SettingsWindow.xaml.cs 的通用构造辅助方法搭建）。</summary>
public partial class SettingsWindow
{
    // =========================================================================
    // 通用
    // =========================================================================

    private FrameworkElement BuildGeneralSection()
    {
        var s = Settings.Current;
        return SectionFrame("通用", "控制班机的启动方式、置顶状态和音效音量。所有修改立即生效。",
            Card(
                Row("始终置顶", "让宠物与提示始终悬浮在其它窗口之上（包括 PPT 放映时）。",
                    Toggle(s.AlwaysOnTop, v => Settings.Update(x => x.AlwaysOnTop = v))),
                Divider(),
                Row("开机自动启动", "写入当前用户的开机启动项（HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run），无需管理员权限。",
                    Toggle(AutoStartRegistration.IsEnabled(), OnAutoStartToggled)),
                Divider(),
                SliderRow("清醒一下音量", "“清醒一下”音效的应用内音量；100% 为程序内最大音量（不改变系统音量）。",
                    0, 100, s.StingVolume * 100, 1, v => $"{v:0}%",
                    v => Settings.Update(x => x.StingVolume = v / 100.0))
            ),
            Card(
                Row("试听“清醒一下”", "随机播放一个 0.3 秒以内的单拍音效（注意音量）。",
                    ActionButton("播放一次", () => AppServices.Current.Audio.PlayRandomSting(), primary: true))
            ));
    }

    // =========================================================================
    // 课堂
    // =========================================================================

    private FrameworkElement BuildClassSection()
    {
        var s = Settings.Current;
        return SectionFrame("课堂", "课时长度、铃声误差与上下课全屏提示。",
            Card(
                SliderRow("标准课时长度",
                    "抽签的公平约束窗口默认使用此长度；若在“抽签”分区开启“跟随当前课节”，则改用课表里这节课的真实时长（精确到秒）。",
                    1, 180, s.ClassDurationMinutes, 1, v => $"{(int)Math.Round(v)} 分钟",
                    v => Settings.Update(x => x.ClassDurationMinutes = (int)Math.Round(v))),
                Divider(),
                SliderRow("铃声误差",
                    "本程序时间比铃声快/慢多少秒（默认 0）：正值表示本程序比铃声慢（打铃更早），负值表示比铃声快。用于对齐学校的铃声。",
                    -60, 60, s.BellOffsetSeconds, 1, FormatBellOffset,
                    v => Settings.Update(x => x.BellOffsetSeconds = (int)Math.Round(v)), 270)
            ),
            Card(
                Row("上课提示", "上课时全屏显示醒目的“上课”提示（带背景）。",
                    Toggle(s.ShowClassStartNotice, v => Settings.Update(x => x.ShowClassStartNotice = v))),
                Divider(),
                Row("下课提示", "下课时全屏显示文字提示，不遮挡点击。",
                    Toggle(s.ShowClassEndNotice, v => Settings.Update(x => x.ShowClassEndNotice = v))),
                Divider(),
                SliderRow("提示停留秒数", "上课/下课全屏提示自动消失的时间。",
                    1, 30, s.NoticeDurationSeconds, 1, v => $"{(int)Math.Round(v)} 秒",
                    v => Settings.Update(x => x.NoticeDurationSeconds = (int)Math.Round(v))),
                Divider(),
                SliderRow("下课超时行显示秒数", "下课后宠物上“距下课”行替换为“下课超时 mm:ss”的停留时间，之后自动恢复。",
                    3, 60, s.EndOverrunSeconds, 1, v => $"{(int)Math.Round(v)} 秒",
                    v => Settings.Update(x => x.EndOverrunSeconds = (int)Math.Round(v)))
            ));
    }

    private static string FormatBellOffset(double seconds)
    {
        var value = (int)Math.Round(seconds);
        if (value == 0) return "0 秒（一致）";
        return value > 0 ? $"慢 {value} 秒" : $"快 {-value} 秒";
    }

    // =========================================================================
    // 抽签
    // =========================================================================

    private FrameworkElement BuildPickSection()
    {
        var s = Settings.Current;
        var rosterInfo = "尚未导入花名册";
        try
        {
            var active = AppServices.Current.Rosters.Active;
            var count = AppServices.Current.Rosters.Rosters.Count;
            if (active is not null)
                rosterInfo = $"当前激活：{active.Name}（{active.Students.Count} 人）· 共 {count} 份花名册";
            else if (count > 0)
                rosterInfo = $"共 {count} 份花名册，尚未激活";
        }
        catch
        {
            // 忽略：只影响展示文案
        }

        return SectionFrame("抽签", "抽签显示方式、公平约束窗口与花名册入口。",
            Card(
                Row("显示模式", "抽中后气泡里显示的内容。",
                    Segmented(new[]
                    {
                        ("学号", PickDisplayMode.StudentId),
                        ("姓名", PickDisplayMode.Name),
                        ("两者", PickDisplayMode.Both)
                    }, s.PickDisplay, v => Settings.Update(x => x.PickDisplay = v))),
                Divider(),
                Row("抽签窗口跟随当前课节", "开启后，公平约束窗口使用当前这节课的真实时长（精确到秒）；关闭则使用“课堂”分区里的标准课时长度。",
                    Toggle(s.PickWindowFollowsCurrentPeriod, v => Settings.Update(x => x.PickWindowFollowsCurrentPeriod = v))),
                Divider(),
                Row("每节课重置会话计数", "每节课开始时清空本节课的抽签计数（滚动窗口的历史计次仍保留）。",
                    Toggle(s.ResetPickSessionOnClassStart, v => Settings.Update(x => x.ResetPickSessionOnClassStart = v))),
                Divider(),
                Row("抽姓名时同时显示学号", "抽中姓名时，学号也一并显示。",
                    Toggle(s.PickShowIdWhenPickingName, v => Settings.Update(x => x.PickShowIdWhenPickingName = v)))
            ),
            Card(
                Row("花名册", rosterInfo, ButtonRow(
                    ActionButton("打开花名册管理", () => AppServices.Current.ShowRosterManager(), primary: true),
                    ActionButton("手动录入", () => AppServices.Current.ShowManualRosterDialog())))
            ));
    }

    // =========================================================================
    // 按钮样式
    // =========================================================================

    private FrameworkElement BuildButtonsSection()
    {
        var b = Settings.Current.Buttons;
        var preview = new ButtonStylePreview();
        preview.Apply(b);
        _buttonPreview = preview;

        var previewCard = Card(
            FullRow("四态实时预览", "修改下方任意参数会立即反映在这里，也会同步到宠物上的真实功能区按钮。", preview));

        var stateCard = Card(
            ColorWithOpacityRow("常态", "字与边框：默认 #317FFF，透明度 10%（0% 为完全不透明）。", b.IdleColor, "#317FFF", b.IdleOpacityPercent,
                v => UpdateButtons(x => x.IdleColor = v), v => UpdateButtons(x => x.IdleOpacityPercent = v)),
            Divider(),
            ColorWithOpacityRow("悬浮", "鼠标悬浮时：默认 #5998FF，透明度 0%（完全不透明）。", b.HoverColor, "#5998FF", b.HoverOpacityPercent,
                v => UpdateButtons(x => x.HoverColor = v), v => UpdateButtons(x => x.HoverOpacityPercent = v)),
            Divider(),
            ColorWithOpacityRow("按下", "左键按下时：默认 #0051D6，透明度 0%。", b.PressedColor, "#0051D6", b.PressedOpacityPercent,
                v => UpdateButtons(x => x.PressedColor = v), v => UpdateButtons(x => x.PressedOpacityPercent = v)),
            Divider(),
            ColorWithOpacityRow("强调", "如“取消计时”：默认 #FF3030，透明度 0%。", b.AccentColor, "#FF3030", b.AccentOpacityPercent,
                v => UpdateButtons(x => x.AccentColor = v), v => UpdateButtons(x => x.AccentOpacityPercent = v))
        );

        var shapeCard = Card(
            SliderRow("边框宽度", "默认 1.5px。", 0, 8, b.BorderThickness, 0.5, v => $"{v:0.0} px",
                v => UpdateButtons(x => x.BorderThickness = v)),
            Divider(),
            SliderRow("圆角 R", "默认 8，与 Win11 输入法预览框保持一致。", 0, 40, b.CornerRadius, 1, v => $"{v:0}",
                v => UpdateButtons(x => x.CornerRadius = v)),
            Divider(),
            SliderRow("阴影偏移", "右下 45°，默认 4px。", 0, 24, b.ShadowOffset, 1, v => $"{v:0} px",
                v => UpdateButtons(x => x.ShadowOffset = v)),
            Divider(),
            SliderRow("阴影不透明度", "默认 12%。", 0, 100, b.ShadowOpacityPercent, 1, v => $"{v:0}%",
                v => UpdateButtons(x => x.ShadowOpacityPercent = (int)Math.Round(v))),
            Divider(),
            ColorRow("阴影颜色", "默认黑色。", b.ShadowColor, "#000000", v => UpdateButtons(x => x.ShadowColor = v)),
            Divider(),
            SliderRow("字号", "默认 14。", 8, 40, b.FontSize, 1, v => $"{v:0}",
                v => UpdateButtons(x => x.FontSize = v))
        );

        var restoreCard = Card(
            FullRow("恢复默认", "把按钮样式的全部参数恢复为开发文档第 4 章附录的默认值。",
                ButtonRow(RestoreDefaultsButton())));

        return SectionFrame("按钮样式", "功能区按钮的四态颜色、透明度、边框、圆角、阴影与字号，全部可自定义。", previewCard, stateCard, shapeCard, restoreCard);
    }

    private Button RestoreDefaultsButton()
    {
        var button = new Button
        {
            Content = "恢复默认",
            Style = StyleRes("SettingDangerButton")
        };
        button.Click += (_, _) => OnRestoreButtonDefaults();
        return button;
    }

    private void UpdateButtons(Action<ButtonStyleSettings> mutate)
    {
        Guard("修改按钮样式", () =>
        {
            Settings.Update(x => mutate(x.Buttons));
            RefreshButtonPreview();
        });
    }

    private void RefreshButtonPreview()
    {
        if (_buttonPreview is null || !AppServices.IsInitialized) return;
        _buttonPreview.Apply(Settings.Current.Buttons);
    }

    private void OnRestoreButtonDefaults()
    {
        if (IsDefaultButtonStyle(Settings.Current.Buttons))
        {
            Info("已经是默认了！");
            return;
        }

        Guard("恢复按钮默认样式", () =>
        {
            Settings.Update(x => x.Buttons = new ButtonStyleSettings());
            Info("恢复默认成功！");
            RebuildSection("buttons");
        });
    }

    private static bool IsDefaultButtonStyle(ButtonStyleSettings b)
    {
        var d = new ButtonStyleSettings();
        return b.IdleColor == d.IdleColor
               && b.IdleOpacityPercent == d.IdleOpacityPercent
               && b.HoverColor == d.HoverColor
               && b.HoverOpacityPercent == d.HoverOpacityPercent
               && b.PressedColor == d.PressedColor
               && b.PressedOpacityPercent == d.PressedOpacityPercent
               && b.AccentColor == d.AccentColor
               && b.AccentOpacityPercent == d.AccentOpacityPercent
               && b.BorderThickness == d.BorderThickness
               && b.CornerRadius == d.CornerRadius
               && b.ShadowOffset == d.ShadowOffset
               && b.ShadowOpacityPercent == d.ShadowOpacityPercent
               && b.ShadowColor == d.ShadowColor
               && b.FontSize == d.FontSize;
    }

    // =========================================================================
    // 气泡
    // =========================================================================

    private FrameworkElement BuildBubbleSection()
    {
        var b = Settings.Current.Bubble;
        return SectionFrame("气泡", "对话气泡、抽签结果与计时显示的外观。",
            Card(
                SliderRow("背景不透明度", "默认 50%。", 0, 100, b.OpacityPercent, 1, v => $"{(int)Math.Round(v)}%",
                    v => Settings.Update(x => x.Bubble.OpacityPercent = (int)Math.Round(v))),
                Divider(),
                Row("高斯模糊背景", "让气泡背景更柔和（Win11 亚克力观感）。",
                    Toggle(b.UseBlur, v => Settings.Update(x => x.Bubble.UseBlur = v))),
                Divider(),
                SliderRow("模糊半径", "仅开启模糊背景时生效。", 0, 60, b.BlurRadius, 1, v => $"{v:0}",
                    v => Settings.Update(x => x.Bubble.BlurRadius = v)),
                Divider(),
                ColorRow("边框颜色", "默认 #3E6BFF。", b.BorderColor, "#3E6BFF", v => Settings.Update(x => x.Bubble.BorderColor = v)),
                Divider(),
                SliderRow("边框粗细", "默认 2px。", 0, 8, b.BorderThickness, 0.5, v => $"{v:0.0} px",
                    v => Settings.Update(x => x.Bubble.BorderThickness = v)),
                Divider(),
                ColorRow("文字颜色", "默认 #2E69FF。", b.TextColor, "#2E69FF", v => Settings.Update(x => x.Bubble.TextColor = v)),
                Divider(),
                ColorRow("背景颜色", "默认白色。", b.BackgroundColor, "#FFFFFF", v => Settings.Update(x => x.Bubble.BackgroundColor = v))
            ),
            Card(
                SliderRow("打字速度", "每个字的间隔毫秒数；0 表示立即显示。", 0, 200, b.TypewriterMsPerChar, 1, v => $"{v:0} ms/字",
                    v => Settings.Update(x => x.Bubble.TypewriterMsPerChar = (int)Math.Round(v))),
                Divider(),
                SliderRow("抽签结果停留时长", "抽中结果气泡的显示时间（开发文档默认 10 秒）。", 1, 30,
                    b.PickBubbleDurationMs / 1000.0, 0.5, v => $"{v:0.#} 秒",
                    v => Settings.Update(x => x.Bubble.PickBubbleDurationMs = (int)Math.Round(v * 1000)))
            ));
    }

    // =========================================================================
    // 宠物
    // =========================================================================

    private FrameworkElement BuildPetSection()
    {
        var s = Settings.Current;
        return SectionFrame("宠物", "宠物大小、颜色与动画；缩放无级、无上下限。",
            Card(ScaleRow(s.UiScale)),
            Card(
                ColorRow("身体颜色", "默认参考 Claude 的橙色。", s.Pet.BodyColor, "#D97757",
                    v => Settings.Update(x => x.Pet.BodyColor = v)),
                Divider(),
                ColorRow("眼睛颜色", "默认纯黑小圆点。", s.Pet.EyeColor, "#231F20",
                    v => Settings.Update(x => x.Pet.EyeColor = v))
            ),
            Card(
                Row("眨眼动画", "宠物偶尔眨眼（眼睛压扁一下）。",
                    Toggle(s.PetBlinkEnabled, v => Settings.Update(x => x.PetBlinkEnabled = v))),
                Divider(),
                Row("呼吸动画", "宠物轻微的上下呼吸浮动。",
                    Toggle(s.PetBobEnabled, v => Settings.Update(x => x.PetBobEnabled = v)))
            ));
    }

    private FrameworkElement ScaleRow(double current)
    {
        var valueText = new TextBlock
        {
            Text = current.ToString("0.00"),
            MinWidth = 52,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = FontMedium,
            FontSize = 13,
            Foreground = BrandBrush
        };
        var input = new TextBox
        {
            Text = current.ToString("0.##"),
            Width = 82,
            Style = StyleRes("Input.TextBox"),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "允许 0.05 ~ 20 之间的任意数值"
        };
        var sync = false;

        var slider = Slider(0.1, 4.0, Math.Clamp(current, 0.1, 4.0), 0.05, v =>
        {
            valueText.Text = v.ToString("0.00");
            if (!sync) input.Text = v.ToString("0.##");
            Settings.Update(x => x.UiScale = Math.Round(v, 3));
        }, 200);

        void CommitInput()
        {
            if (sync) return;
            if (!double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                input.Text = Settings.Current.UiScale.ToString("0.##");
                return;
            }
            value = Math.Clamp(value, 0.05, 20.0);
            input.Text = value.ToString("0.##");
            valueText.Text = value.ToString("0.00");
            sync = true;
            slider.Value = Math.Clamp(value, 0.1, 4.0);
            sync = false;
            Settings.Update(x => x.UiScale = value);
        }

        input.LostFocus += (_, _) => Guard("修改宠物缩放", CommitInput);
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Guard("修改宠物缩放", CommitInput);
            e.Handled = true;
        };

        var hint = new TextBlock
        {
            Text = "或输入 0.05~20",
            FontFamily = FontRegular,
            FontSize = 11.5,
            Foreground = TextTertiary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        panel.Children.Add(input);
        panel.Children.Add(hint);
        return Row("缩放", "滑块 0.1~4，也可直接输入 0.05~20 之间的任意数值（无级缩放）。", panel);
    }

    // =========================================================================
    // 计时与音效
    // =========================================================================

    private FrameworkElement BuildTimerSection()
    {
        var s = Settings.Current;
        return SectionFrame("计时与音效", "“清醒一下”试听与计时器超时全屏样式。",
            Card(
                Row("清醒一下", "程序内合成、随机播放的 0.3 秒以内单拍音效；音量在“通用”分区调节。",
                    ActionButton("播放一次", () => AppServices.Current.Audio.PlayRandomSting(), primary: true)),
                Divider(),
                SliderRow("超时全屏字号", "“计时器超时”文字大小。", 40, 200, s.Notice.TimerTimeoutFontSize, 1, v => $"{v:0}",
                    v => Settings.Update(x => x.Notice.TimerTimeoutFontSize = v))
            ),
            Card(
                SliderRow("超时全屏遮罩不透明度", "计时器超时全屏模糊层上方遮罩的暗度。", 0, 100,
                    s.Notice.BlurBackdropOpacityPercent, 1, v => $"{v:0}%",
                    v => Settings.Update(x => x.Notice.BlurBackdropOpacityPercent = (int)Math.Round(v)))
            ));
    }

    // =========================================================================
    // 提示预览
    // =========================================================================

    private FrameworkElement BuildPreviewSection()
    {
        return SectionFrame("提示预览", "预览全屏提示的实际效果（会短暂盖住屏幕，可按提示自动关闭）。",
            Card(
                Row("上课提示", "全屏显示醒目的“上课”提示。",
                    ActionButton("预览上课提示", () => AppServices.Current.PreviewClassStartNotice(), primary: true)),
                Divider(),
                Row("下课提示", "全屏显示“下课”文字提示，不遮挡点击。",
                    ActionButton("预览下课提示", () => AppServices.Current.PreviewClassEndNotice())),
                Divider(),
                Row("计时器超时", "全屏高斯模糊 + 居中的“计时器超时”，15 秒自动关闭，也可提前关闭。",
                    ActionButton("预览计时器超时", () => AppServices.Current.PreviewTimerTimeout()))
            ));
    }
}
