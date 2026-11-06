using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Banji.Core.Abstractions;
using Banji.Core.Backup;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
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
            ),
            Card(
                FullRow("铃声校准向导",
                    "觉得班机上下课时间总比学校铃声快/慢几秒？点“我听到铃声了”，向导会算出需要写入的铃声误差（±60 秒内），自动对齐学校铃声。",
                    ButtonRow(ActionButton("打开铃声校准向导",
                        () => AppServices.Current.ShowRingCalibrationWindow(), primary: true)))
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
        return SectionFrame("气泡", "对话气泡、抽签结果与计时显示的外观（第二轮默认改为不透明白底、关闭高斯模糊）。",
            Card(
                SliderRow("背景透明度",
                    "0 = 完全不透明（默认白底，文字最清楚）；50 = 半透明；100 = 完全透明。",
                    0, 100, b.OpacityPercent, 1, v => $"{(int)Math.Round(v)}%",
                    v => Settings.Update(x => x.Bubble.OpacityPercent = (int)Math.Round(v))),
                Divider(),
                Row("高斯模糊背景", "默认关闭（白底更清楚）。开启后用屏幕截图做高斯模糊底，清晰度和性能都会变差。",
                    Toggle(b.UseBlur, v => Settings.Update(x => x.Bubble.UseBlur = v))),
                Divider(),
                SliderRow("模糊半径", "仅开启“高斯模糊背景”时生效。", 0, 60, b.BlurRadius, 1, v => $"{v:0}",
                    v => Settings.Update(x => x.Bubble.BlurRadius = v)),
                Divider(),
                ColorRow("边框颜色", "默认 #3E6BFF。", b.BorderColor, "#3E6BFF", v => Settings.Update(x => x.Bubble.BorderColor = v)),
                Divider(),
                SliderRow("边框粗细", "默认 2px。", 0, 8, b.BorderThickness, 0.5, v => $"{v:0.0} px",
                    v => Settings.Update(x => x.Bubble.BorderThickness = v)),
                Divider(),
                ColorRow("文字颜色", "默认 #2E69FF。", b.TextColor, "#2E69FF", v => Settings.Update(x => x.Bubble.TextColor = v)),
                Divider(),
                ColorRow("背景颜色", "默认白色（配合 0% 透明度 = 不透明白底）。", b.BackgroundColor, "#FFFFFF",
                    v => Settings.Update(x => x.Bubble.BackgroundColor = v))
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
        return SectionFrame("宠物", "宠物形象大小、课表大小与位置分别可调；颜色、动画与陪伴养成。v1.2 起“整体缩放”已拆成“宠物形象大小”和“课表大小”。",
            BuildScaleAndPositionCard(s),
            Card(
                ColorRow("身体颜色", "默认雾蓝色 #8FB6F2（第二轮：方形、扁扁的小方块）。", s.Pet.BodyColor, "#8FB6F2",
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
                    Toggle(s.PetBobEnabled, v => Settings.Update(x => x.PetBobEnabled = v))),
                Divider(),
                Row("本地台词生成器", "用模板 + 槽位组合生成台词，零联网，比固定台词库更不容易重复。",
                    Toggle(s.UseLocalQuipGenerator, v => Settings.Update(x => x.UseLocalQuipGenerator = v))),
                Divider(),
                Row("宠物养成", "统计陪伴天数、摸头/抽签/分组/提示次数与成就（纯本地）。关闭后停止统计，已有数据保留。",
                    Toggle(s.EnableGrowth, v => Settings.Update(x => x.EnableGrowth = v)))
            ),
            BuildGrowthCard());
    }

    /// <summary>v1.2.0：宠物形象大小 / 课表大小 / 课表左右上下位置 + 恢复默认。</summary>
    private FrameworkElement BuildScaleAndPositionCard(AppSettings s)
    {
        var restoreButton = new Button
        {
            Content = "恢复默认大小与位置",
            Style = StyleRes("SettingDangerButton"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        restoreButton.Click += (_, _) => OnRestoreScaleAndPosition();

        return Card(
            ScaleInputRow("宠物形象大小",
                "只影响宠物本体（滑块 0.2~5，也可直接输入数值）。以前是整体缩放，v1.2 起与“课表大小”分开调节。",
                0.2, 5.0, s.PetScale, 0.05,
                v => Settings.Update(x => x.PetScale = Math.Round(v, 3))),
            Divider(),
            SliderRow("课表大小",
                "只影响宠物头顶的课表与常驻信息面板（0.4~4），不影响宠物本体。",
                0.4, 4.0, s.PanelScale, 0.05, v => $"{v:0.00}",
                v => Settings.Update(x => x.PanelScale = Math.Round(v, 3))),
            Divider(),
            SliderRow("课表左右位置",
                "课表/信息面板相对默认位置的横向偏移（DIP），正数向右、负数向左。",
                -300, 300, Math.Clamp(s.PanelOffsetX, -300, 300), 5, FormatOffset,
                v => Settings.Update(x => x.PanelOffsetX = Math.Round(v))),
            Divider(),
            SliderRow("课表上下位置",
                "课表/信息面板相对默认位置的纵向偏移（DIP），正数向下、负数向上。",
                -300, 300, Math.Clamp(s.PanelOffsetY, -300, 300), 5, FormatOffset,
                v => Settings.Update(x => x.PanelOffsetY = Math.Round(v))),
            Divider(),
            Row("恢复默认", "宠物形象大小 = 1，课表大小 = 1，课表左右/上下位置 = 0。", restoreButton)
        );
    }

    private static string FormatOffset(double value)
    {
        var px = (int)Math.Round(value);
        return px > 0 ? $"+{px} px" : $"{px} px";
    }

    private void OnRestoreScaleAndPosition()
    {
        Guard("恢复默认大小与位置", () =>
        {
            Settings.Update(x =>
            {
                x.PetScale = 1;
                x.PanelScale = 1;
                x.PanelOffsetX = 0;
                x.PanelOffsetY = 0;
                x.UiScale = 1; // 历史字段兼容
            });
            Info("已恢复默认大小与位置。");
            RebuildSection("pet");
        });
    }

    // =========================================================================
    // 宠物养成统计（设置窗口内）
    // =========================================================================

    private FrameworkElement BuildGrowthCard()
    {
        _growthStatsText = new TextBlock
        {
            FontFamily = FontMedium,
            FontSize = 14,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap
        };
        _growthExtraText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 12,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _growthAchievementText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 12,
            Foreground = TextTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var resetButton = new Button
        {
            Content = "清零养成数据",
            Style = StyleRes("SettingDangerButton"),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        resetButton.Click += (_, _) => OnResetGrowthData();

        var content = new StackPanel();
        content.Children.Add(_growthStatsText);
        content.Children.Add(_growthExtraText);
        content.Children.Add(_growthAchievementText);
        content.Children.Add(resetButton);

        RefreshGrowthStatsDisplay();
        return Card(FullRow("养成统计", "陪伴数据只保存在本机，不会上传；关闭上方“宠物养成”后停止累计。", content));
    }

    private void RefreshGrowthStatsDisplay()
    {
        if (_growthStatsText is null || !AppServices.IsInitialized) return;
        try
        {
            var growth = AppServices.Current.Growth;
            var stats = growth.Stats;
            var achievements = growth.Achievements;
            _growthStatsText!.Text =
                $"陪伴 {stats.DaysUsed} 天 · 摸头 {stats.PatCount} 次 · 抽签 {stats.PickCount} 次";
            if (_growthExtraText is not null)
            {
                _growthExtraText.Text =
                    $"分组 {stats.GroupCount} 次 · 上下课提示 {stats.NoticeCount} 次 · 启动 {stats.SessionCount} 次" +
                    (stats.FirstUseDate != default ? $" · 从 {stats.FirstUseDate:yyyy-MM-dd} 开始陪伴" : "");
            }
            if (_growthAchievementText is not null)
            {
                _growthAchievementText.Text = achievements.Count == 0
                    ? "还没有解锁成就，多陪它上几节课试试～"
                    : $"已解锁 {achievements.Count} 个成就：{string.Join("、", achievements.Select(a => a.Title))}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置窗口刷新养成统计失败", ex);
        }
    }

    private void OnResetGrowthData()
    {
        var confirm = MessageBox.Show(
            "确定清零宠物养成数据吗？\n\n陪伴天数、摸头/抽签/分组/提示次数与成就都会重置，花名册积分等其它数据不受影响。",
            "清零养成数据", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        Guard("清零养成数据", () =>
        {
            AppServices.Current.Growth.Reset();
            RefreshGrowthStatsDisplay();
            Info("养成数据已清零。");
        });
    }

    /// <summary>滑块 + 数值输入框的一行（v1.2.0 分离缩放用）。</summary>
    private FrameworkElement ScaleInputRow(
        string title,
        string? description,
        double min,
        double max,
        double current,
        double step,
        Action<double> onChange)
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
            ToolTip = $"允许 {min} ~ {max} 之间的任意数值"
        };
        var sync = false;

        var slider = Slider(min, max, Math.Clamp(current, min, max), step, v =>
        {
            valueText.Text = v.ToString("0.00");
            if (!sync) input.Text = v.ToString("0.##");
            onChange(v);
        }, 190);

        void CommitInput()
        {
            if (sync) return;
            if (!double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                input.Text = current.ToString("0.##");
                return;
            }
            value = Math.Clamp(value, min, max);
            input.Text = value.ToString("0.##");
            valueText.Text = value.ToString("0.00");
            sync = true;
            slider.Value = value;
            sync = false;
            onChange(value);
        }

        input.LostFocus += (_, _) => Guard($"修改「{title}」", CommitInput);
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Guard($"修改「{title}」", CommitInput);
            e.Handled = true;
        };

        var hint = new TextBlock
        {
            Text = $"或输入 {min}~{max}",
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
        return Row(title, description, panel);
    }

    // =========================================================================
    // 端侧 AI（v1.2.0）
    // =========================================================================

    private FrameworkElement BuildAiSection()
    {
        var ai = Settings.Current.Ai;

        _aiStatusText = new TextBlock
        {
            FontFamily = FontMedium,
            FontSize = 14,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap
        };
        _aiModelText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 12.5,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _aiModelSizeText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 12,
            Foreground = TextTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _aiModelDirText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 11.5,
            Foreground = TextTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _aiModelListPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var statusContent = new StackPanel();
        statusContent.Children.Add(_aiStatusText);
        statusContent.Children.Add(_aiModelText);
        statusContent.Children.Add(_aiModelSizeText);
        statusContent.Children.Add(_aiModelDirText);
        statusContent.Children.Add(_aiModelListPanel);

        var releaseButton = new Button
        {
            Content = "释放模型内存",
            Style = StyleRes("SettingGhostButton"),
            Margin = new Thickness(0, 0, 10, 0)
        };
        releaseButton.Click += (_, _) => OnReleaseAiModel();

        var helpContent = BuildAiHelpContent();
        RefreshAiStatusDisplay();

        return SectionFrame("端侧 AI",
            "在本机跑一个小型大语言模型：生成宠物台词、和你聊天。模型只在下载时需要联网，推理全程离线。",
            Card(
                FullRow("模型状态", null, statusContent),
                Divider(),
                Row("模型管理", "查看可下载模型、一键下载到本地，或选择已有的 GGUF 文件（下载源 hf-mirror，国内可直连）。",
                    ActionButton("打开模型管理", () => AppServices.Current.ShowAiModelManager(), primary: true)),
                Divider(),
                Row("与班机聊天", "打开聊天窗口和端侧模型对话（聊天记录只在内存中，不上传）。",
                    ActionButton("打开聊天", () => AppServices.Current.ShowChatWindow())),
                Divider(),
                Row("释放模型内存", "把模型从内存卸载（下载的文件仍保留，下次可重新加载；释放后 AI 台词自动回退本地台词库）。",
                    releaseButton)
            ),
            Card(
                Row("启用 AI 台词", "摸头台词优先尝试端侧 AI 生成；未加载模型时自动回退本地台词生成器。",
                    Toggle(ai.EnableAiQuips, v => Settings.Update(x => x.Ai.EnableAiQuips = v))),
                Divider(),
                Row("启动时自动加载", "班机启动后在后台加载上次使用的模型，不阻塞宠物和课堂功能。",
                    Toggle(ai.AutoLoadModel, v => Settings.Update(x => x.Ai.AutoLoadModel = v))),
                Divider(),
                SliderRow("温度", "越高回答越随机有创意，越低越稳定（0~1.5，默认 0.8）。",
                    0, 1.5, ai.Temperature, 0.05, v => $"{v:0.00}",
                    v => Settings.Update(x => x.Ai.Temperature = Math.Round(v, 2))),
                Divider(),
                Row("上下文长度", "能记住的对话长度（tokens）。越长越占内存、响应越慢，16GB 内存推荐 2048。",
                    Segmented(new[]
                    {
                        ("512", 512),
                        ("1024", 1024),
                        ("2048", 2048),
                        ("4096", 4096)
                    }, ai.ContextSize, v => Settings.Update(x => x.Ai.ContextSize = v)))
            ),
            Card(FullRow("模型下载与离线说明", null, helpContent)));
    }

    private FrameworkElement BuildAiHelpContent()
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "• 模型不随安装包分发，需在“打开模型管理”里一键下载（联网仅用于下载，可用 hf-mirror 国内镜像）。",
            FontFamily = FontRegular,
            FontSize = 12.5,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21
        });
        content.Children.Add(new TextBlock
        {
            Text = "• 下载完成后本地 CPU 推理，聊天与 AI 台词全程不联网；聊天记录不上传、不落盘。",
            FontFamily = FontRegular,
            FontSize = 12.5,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21
        });
        content.Children.Add(new TextBlock
        {
            Text = "• 模型目录：" + SafeAiModelDirectory(),
            FontFamily = FontRegular,
            FontSize = 11.5,
            Foreground = TextTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = "• 16GB 内存建议 1.5B；模型加载在后台进行，加载期间宠物/课堂功能不受影响。",
            FontFamily = FontRegular,
            FontSize = 11.5,
            Foreground = TextTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return content;
    }

    private static string SafeAiModelDirectory()
    {
        try
        {
            return AppServices.IsInitialized
                ? AppServices.Current.Ai.ModelDirectory
                : Path.Combine(AppPaths.Root, "models");
        }
        catch
        {
            return Path.Combine(AppPaths.Root, "models");
        }
    }

    private void RefreshAiStatusDisplay()
    {
        if (_aiStatusText is null || !AppServices.IsInitialized) return;
        try
        {
            var ai = AppServices.Current.Ai;
            var (statusText, brush) = ai.Status switch
            {
                AiModelStatus.Ready => ("已就绪，可以聊天 / 生成 AI 台词", BrushRes("Brush.Brand", Colors.RoyalBlue)),
                AiModelStatus.Loading => ("正在后台加载模型…（加载期间宠物和课堂功能不受影响）", BrushRes("Brush.Warning", Colors.Orange)),
                AiModelStatus.Failed => ("加载失败，可在“模型管理”里更换模型或重试", BrushRes("Brush.Accent", Colors.Red)),
                _ => ("未安装模型（请在“模型管理”里下载，或选择本地 GGUF 文件）", BrushRes("Brush.TextTertiary", Colors.Gray))
            };
            _aiStatusText!.Text = "状态：" + statusText;
            _aiStatusText.Foreground = brush;

            var settingsAi = Settings.Current.Ai;
            var loadedName = !string.IsNullOrWhiteSpace(ai.LoadedModelName)
                ? ai.LoadedModelName
                : !string.IsNullOrWhiteSpace(settingsAi.ModelName) ? $"{settingsAi.ModelName}（上次使用，未加载）" : "";
            if (_aiModelText is not null)
            {
                _aiModelText.Text = loadedName.Length > 0 ? $"模型：{loadedName}" : "模型：无";
            }
            if (_aiModelSizeText is not null)
            {
                _aiModelSizeText.Text = "模型大小：" + FormatAiModelSize(ai, settingsAi);
            }
            if (_aiModelDirText is not null)
            {
                _aiModelDirText.Text = "模型目录：" + SafeAiModelDirectory();
            }

            // 可下载模型列表（来自 IAiService.ModelOptions）
            if (_aiModelListPanel is not null)
            {
                _aiModelListPanel.Children.Clear();
                var options = ai.ModelOptions;
                if (options is { Count: > 0 })
                {
                    _aiModelListPanel.Children.Add(new TextBlock
                    {
                        Text = "可下载模型：",
                        FontFamily = FontRegular,
                        FontSize = 11.5,
                        Foreground = TextTertiary,
                        Margin = new Thickness(0, 0, 0, 3)
                    });
                    foreach (var option in options)
                    {
                        var line = new TextBlock
                        {
                            Text = $"• {option.Name}　≈ {option.SizeText}" + (option.Recommended ? "　（推荐）" : ""),
                            FontFamily = FontRegular,
                            FontSize = 11.5,
                            Foreground = option.Recommended ? BrushRes("Brush.Brand", Colors.RoyalBlue) : TextTertiary,
                            TextWrapping = TextWrapping.Wrap
                        };
                        _aiModelListPanel.Children.Add(line);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置窗口刷新 AI 状态失败", ex);
        }
    }

    private static string FormatAiModelSize(IAiService ai, AiSettings settingsAi)
    {
        try
        {
            var path = ai.LoadedModelPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) path = settingsAi.ModelPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (length > 0) return FormatFileSize(length);
            }
            // 未加载/文件不存在时，用上次选择的模型 Id 从可下载列表推断大小
            if (!string.IsNullOrWhiteSpace(settingsAi.LastModelId))
            {
                var option = ai.ModelOptions?.FirstOrDefault(o =>
                    string.Equals(o.Id, settingsAi.LastModelId, StringComparison.OrdinalIgnoreCase));
                if (option is { SizeBytes: > 0 }) return $"≈ {option.SizeText}（未加载）";
            }
        }
        catch
        {
            // 仅展示用
        }
        return "—";
    }

    private void OnReleaseAiModel()
    {
        Guard("释放模型内存", () =>
        {
            AppServices.Current.Ai.Unload();
            RefreshAiStatusDisplay();
            Info("已释放模型内存。AI 台词会自动回退本地台词生成器。");
        });
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

    // =========================================================================
    // 提示预览
    // =========================================================================

    private FrameworkElement BuildPreviewSection()
    {
        return SectionFrame("提示预览", "预览全屏提示的实际效果（会短暂盖住屏幕，可按提示自动关闭）。",
            Card(
                Row("上课提示", "全屏显示醒目的“上课”提示（带自动关闭进度条）。",
                    ActionButton("预览上课提示", () => AppServices.Current.PreviewClassStartNotice(), primary: true)),
                Divider(),
                Row("下课提示", "全屏显示“下课”文字提示，不遮挡点击，自动关闭。",
                    ActionButton("预览下课提示", () => AppServices.Current.PreviewClassEndNotice())),
                Divider(),
                Row("计时器超时", "全屏高斯模糊 + 居中的“计时器超时”，15 秒自动关闭，也可提前关闭。",
                    ActionButton("预览计时器超时", () => AppServices.Current.PreviewTimerTimeout()))
            ));
    }

    // =========================================================================
    // 考试倒计时（第二轮新增）
    // =========================================================================

    private FrameworkElement BuildCountdownSection()
    {
        var s = Settings.Current;
        _countdownDraft = (s.CountdownEvents ?? new List<CountdownEventItem>())
            .Select(e => e.Clone())
            .ToList();
        _countdownListHost = new StackPanel();
        RebuildCountdownList();

        var addButton = new Button
        {
            Content = "+ 添加考试 / 重要日期",
            Style = StyleRes("SettingGhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        addButton.Click += (_, _) => AddCountdownEvent();

        var editor = new StackPanel();
        editor.Children.Add(_countdownListHost);
        editor.Children.Add(addButton);

        return SectionFrame("考试倒计时",
            "考试/重要日期的 D-Day 倒计时：宠物常驻区与仪表盘会显示最近的启用事件。",
            Card(
                Row("在宠物上常驻显示", "开启后，宠物常驻信息区显示最近一场考试/重要日期的 D-Day。",
                    Toggle(s.ShowCountdownOnPet, v => Settings.Update(x => x.ShowCountdownOnPet = v)))
            ),
            Card(
                FullRow("倒计时事件",
                    "名称、日期、颜色和启用状态修改后立即保存；宠物只显示最近的启用事件，过期事件可以保留（显示“已过去”）或删除。",
                    editor)
            ));
    }

    private void RebuildCountdownList()
    {
        if (_countdownListHost is null) return;
        _countdownListHost.Children.Clear();

        if (_countdownDraft.Count == 0)
        {
            _countdownListHost.Children.Add(new TextBlock
            {
                Text = "还没有倒计时事件，点下方按钮添加一个吧（例如：期末考试、高考、校运会）。",
                FontFamily = FontRegular,
                FontSize = 12.5,
                Foreground = TextTertiary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8)
            });
            return;
        }

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddHeaderCell(header, "名称", 0, 150);
        AddHeaderCell(header, "目标日期", 1, 138);
        AddHeaderCell(header, "颜色", 2, 44);
        AddHeaderCell(header, "启用", 3, double.NaN);
        _countdownListHost.Children.Add(header);

        foreach (var item in _countdownDraft)
            _countdownListHost.Children.Add(BuildCountdownRow(item));
    }

    private void AddHeaderCell(Grid header, string text, int column, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = FontRegular,
            FontSize = 11.5,
            Foreground = TextTertiary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(block, column);
        header.Children.Add(block);
    }

    private FrameworkElement BuildCountdownRow(CountdownEventItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 名称
        var nameBox = new TextBox
        {
            Text = item.Name,
            Style = StyleRes("Input.TextBox"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "事件名称（例如：期末考试）"
        };
        nameBox.LostFocus += (_, _) => CommitCountdownName(item, nameBox);
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitCountdownName(item, nameBox);
            e.Handled = true;
        };
        Grid.SetColumn(nameBox, 0);
        row.Children.Add(nameBox);

        // 日期
        var datePicker = new DatePicker
        {
            SelectedDate = item.Date.ToDateTime(TimeOnly.MinValue),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "目标日期",
            FontFamily = FontRegular,
            FontSize = 12.5
        };
        datePicker.SelectedDateChanged += (_, _) =>
        {
            if (datePicker.SelectedDate is not { } date) return;
            var newDate = DateOnly.FromDateTime(date);
            if (newDate == item.Date) return;
            item.Date = newDate;
            SaveCountdownDraft();
        };
        Grid.SetColumn(datePicker, 1);
        row.Children.Add(datePicker);

        // 颜色（紧凑调色板，无输入框）
        var colorField = new ColorField(this, item.Color, "#FF6B6B", value =>
        {
            if (item.Color == value) return;
            item.Color = value;
            SaveCountdownDraft();
        }, showTextBox: false)
        {
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(colorField, 2);
        row.Children.Add(colorField);

        // 启用
        var enabled = new CheckBox
        {
            IsChecked = item.Enabled,
            Content = "启用",
            Style = StyleRes("ToggleSwitch"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        enabled.Checked += (_, _) => SetCountdownEnabled(item, true);
        enabled.Unchecked += (_, _) => SetCountdownEnabled(item, false);
        Grid.SetColumn(enabled, 3);
        row.Children.Add(enabled);

        // 删除
        var delete = new Button
        {
            Content = "删除",
            Style = StyleRes("SettingGhostButton"),
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(10, 0, 0, 0)
        };
        delete.Click += (_, _) => DeleteCountdownEvent(item);
        Grid.SetColumn(delete, 4);
        row.Children.Add(delete);

        return row;
    }

    private void CommitCountdownName(CountdownEventItem item, TextBox box)
    {
        var name = (box.Text ?? "").Trim();
        if (name.Length == 0)
        {
            box.Text = item.Name;
            return;
        }
        if (name == item.Name) return;
        item.Name = name;
        SaveCountdownDraft();
    }

    private void SetCountdownEnabled(CountdownEventItem item, bool value)
    {
        if (item.Enabled == value) return;
        item.Enabled = value;
        SaveCountdownDraft();
    }

    private void SaveCountdownDraft()
    {
        Guard("保存考试倒计时", () =>
        {
            if (!AppServices.IsInitialized) return;
            Settings.Update(x => x.CountdownEvents = _countdownDraft.Select(e => e.Clone()).ToList());
            // Settings.Normalize 可能删除空名称项，保持草稿与设置一致
            var normalized = Settings.Current.CountdownEvents;
            if (normalized.Count != _countdownDraft.Count)
            {
                _countdownDraft = normalized.Select(e => e.Clone()).ToList();
                RebuildCountdownList();
            }
        });
    }

    private void AddCountdownEvent()
    {
        Guard("添加考试倒计时", () =>
        {
            var item = new CountdownEventItem
            {
                Name = "新考试",
                Date = DateOnly.FromDateTime(DateTime.Now).AddDays(30),
                Enabled = true,
                Color = "#FF6B6B"
            };
            _countdownDraft.Add(item);
            SaveCountdownDraft();
            RebuildCountdownList();
        });
    }

    private void DeleteCountdownEvent(CountdownEventItem item)
    {
        var confirm = MessageBox.Show($"确定删除“{item.Name}”吗？", "删除倒计时",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _countdownDraft.Remove(item);
        SaveCountdownDraft();
        RebuildCountdownList();
    }

    // =========================================================================
    // 备份与恢复（第二轮新增）
    // =========================================================================

    private FrameworkElement BuildBackupSection()
    {
        var infoText = new TextBlock
        {
            FontFamily = FontRegular,
            FontSize = 12.5,
            LineHeight = 22,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Text = "• 备份包含：设置、花名册、课表、临时换课、抽签历史、宠物养成\n" +
                   "• 备份文件是 .banji（本质是 zip），可以拷到 U 盘/网盘，换电脑或重装后恢复\n" +
                   "• 恢复会覆盖当前全部本地数据，建议先“导出备份”一份当前数据\n" +
                   "• 恢复后请立即重启班机；重启前不要再改设置，避免旧数据写回覆盖备份\n" +
                   $"• 数据目录：{AppPaths.Root}"
        };

        return SectionFrame("备份与恢复",
            "一键备份全部本地数据（设置/花名册/课表/抽签历史/养成），重装或换电脑后可一键恢复。",
            Card(
                Row("导出备份", "选择保存位置，把当前全部数据打包成一个 .banji 备份文件。",
                    ActionButton("导出备份…", OnExportBackup, primary: true)),
                Divider(),
                Row("从备份恢复", "先预览备份信息（时间/版本/名单数/课表），确认后覆盖当前数据；恢复后需要重启班机生效。",
                    ActionButton("从备份恢复…", OnRestoreBackup)),
                Divider(),
                Row("立即重启班机", "恢复备份后点这里让新数据立即生效（重启前不会把当前内存里的旧数据写回磁盘）。",
                    ActionButton("立即重启班机", () =>
                    {
                        var confirm = MessageBox.Show("确定立即重启班机吗？", "重启班机",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                        if (confirm == MessageBoxResult.Yes) AppServices.Current.RestartApplication();
                    }))
            ),
            Card(FullRow("备份说明", null, infoText)));
    }

    private void OnExportBackup()
    {
        Guard("导出备份", () =>
        {
            var svc = AppServices.Current;
            var suggested = (svc.Backup as BackupService)?.SuggestFileName()
                            ?? $"班机备份-{DateTime.Now:yyyyMMdd-HHmm}{BackupService.Extension}";
            var dialog = new SaveFileDialog
            {
                Title = "导出班机备份",
                Filter = "班机备份文件 (*.banji)|*.banji|所有文件 (*.*)|*.*",
                DefaultExt = "banji",
                AddExtension = true,
                FileName = suggested,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            if (string.IsNullOrWhiteSpace(dialog.FileName)) return;

            var size = svc.Backup.ExportTo(dialog.FileName);
            MessageBox.Show(
                $"备份成功！\n\n文件：{dialog.FileName}\n大小：{FormatFileSize(size)}\n\n把这个 .banji 文件保存好，换电脑或重装后可以恢复。",
                "备份成功", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void OnRestoreBackup()
    {
        Guard("恢复备份", () =>
        {
            var svc = AppServices.Current;
            var dialog = new OpenFileDialog
            {
                Title = "选择班机备份文件",
                Filter = "班机备份文件 (*.banji)|*.banji|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

            BackupInfo info;
            try
            {
                info = svc.Backup.Inspect(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取备份失败：{ex.Message}", "恢复备份",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            long size = 0;
            try { size = new FileInfo(dialog.FileName).Length; } catch { /* 仅展示用 */ }

            var text =
                "备份文件信息：\n\n" +
                $"创建时间：{info.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                $"班机版本：{info.AppVersion}\n" +
                $"花名册：{info.RosterCount} 份（{info.StudentCount} 人）\n" +
                $"课表：{(info.HasTimetable ? "有" : "无")}　设置：{(info.HasSettings ? "有" : "无")}\n" +
                $"文件大小：{FormatFileSize(size)}\n\n" +
                "恢复会覆盖当前全部本地数据（设置/花名册/课表/抽签历史/养成），确定继续吗？";
            var confirm = MessageBox.Show(text, "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                svc.Backup.RestoreFrom(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"恢复备份失败：{ex.Message}", "恢复备份",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var restart = MessageBox.Show(
                "恢复成功！重启班机后生效。\n\n是否现在重启班机？（重启不会把当前内存里的旧数据写回磁盘，避免覆盖刚恢复的备份）",
                "恢复成功", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (restart == MessageBoxResult.Yes)
                svc.RestartApplication(saveBeforeRestart: false);
        });
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        if (bytes >= 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} 字节";
    }
}
