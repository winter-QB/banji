using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Banji.Controls.Toolbar;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Settings;

namespace Banji.Views;

/// <summary>
/// 设置窗口：左侧分区导航 + 右侧内容（Win11 风格，鸿蒙字体）。
/// 所有修改立即通过 <see cref="ISettingsService.Update"/> 生效并自动落盘；关闭窗口时再强制保存一次。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _built = new();
    private string _currentSection = "general";
    private string? _initialSection;
    private ButtonStylePreview? _buttonPreview;

    /// <summary>可选：打开时定位到的分区（general/class/pick/buttons/bubble/pet/timer/preview）。</summary>
    public string? InitialSection
    {
        get => _initialSection;
        set
        {
            _initialSection = value;
            if (IsLoaded) SelectSection(value);
        }
    }

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        NavList.SelectionChanged += OnNavSelectionChanged;
    }

    private static ISettingsService Settings => AppServices.Current.Settings;

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncAutoStartState();
        }
        catch (Exception ex)
        {
            Log.Error("同步开机自启动状态失败", ex);
        }

        _built.Clear();
        SelectSection(_initialSection ?? _currentSection);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            if (AppServices.IsInitialized) AppServices.Current.Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("设置窗口关闭时保存失败", ex);
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string key }) ShowSection(key);
    }

    private void SelectSection(string? key)
    {
        var normalized = NormalizeSectionKey(key);
        _currentSection = normalized;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if (Equals(item.Tag, normalized))
            {
                NavList.SelectedItem = item;
                break;
            }
        }
        ShowSection(normalized);
    }

    private static string NormalizeSectionKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "class" or "clock" => "class",
        "pick" or "roster" => "pick",
        "buttons" or "button" => "buttons",
        "bubble" => "bubble",
        "pet" => "pet",
        "timer" or "audio" => "timer",
        "preview" or "notice" => "preview",
        _ => "general"
    };

    private void ShowSection(string key)
    {
        if (!_built.TryGetValue(key, out var section))
        {
            section = BuildSection(key);
            _built[key] = section;
        }
        ContentHost.Children.Clear();
        ContentHost.Children.Add(section);
        ContentScroll.ScrollToTop();
    }

    private FrameworkElement BuildSection(string key) => key switch
    {
        "class" => BuildClassSection(),
        "pick" => BuildPickSection(),
        "buttons" => BuildButtonsSection(),
        "bubble" => BuildBubbleSection(),
        "pet" => BuildPetSection(),
        "timer" => BuildTimerSection(),
        "preview" => BuildPreviewSection(),
        _ => BuildGeneralSection()
    };

    private void RebuildSection(string key)
    {
        _built.Remove(key);
        if (_currentSection == key) ShowSection(key);
    }

    // =========================================================================
    // 主题资源
    // =========================================================================

    private T Res<T>(string key, T fallback) where T : class => TryFindResource(key) as T ?? fallback;

    private FontFamily FontRegular => Res("Font.Regular", new FontFamily("Segoe UI"));
    private FontFamily FontMedium => Res("Font.Medium", FontRegular);
    private FontFamily FontBold => Res("Font.Bold", FontRegular);
    private Brush TextPrimary => Res("Brush.TextPrimary", Brushes.Black);
    private Brush TextSecondary => Res("Brush.TextSecondary", Brushes.Gray);
    private Brush TextTertiary => Res("Brush.TextTertiary", Brushes.Gray);
    private Brush BrandBrush => Res("Brush.Brand", Brushes.RoyalBlue);
    private Brush AccentBrush => Res("Brush.Accent", Brushes.Red);
    private Brush DividerBrush => Res("Brush.Divider", Brushes.Gainsboro);

    private Style? StyleRes(string key) => TryFindResource(key) as Style;

    // =========================================================================
    // 通用 UI 构造
    // =========================================================================

    private FrameworkElement SectionFrame(string title, string? subtitle, params UIElement[] cards)
    {
        var panel = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = FontBold,
            FontWeight = FontWeights.Bold,
            FontSize = 24,
            Foreground = TextPrimary
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle ?? "",
            FontFamily = FontRegular,
            FontSize = 12.5,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 16),
            Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible
        });
        foreach (var card in cards) panel.Children.Add(card);
        return panel;
    }

    private Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        return new Border { Style = StyleRes("Card"), Child = stack };
    }

    private Border Divider() => new()
    {
        Height = 1,
        Background = DividerBrush,
        Margin = new Thickness(0, 4, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>一行设置：左侧标题+说明，右侧控件。</summary>
    private FrameworkElement Row(string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = FontMedium,
            FontSize = 13.5,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            texts.Children.Add(new TextBlock
            {
                Text = description,
                FontFamily = FontRegular,
                FontSize = 11.5,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                MaxWidth = 470
            });
        }

        control.Margin = new Thickness(22, 0, 0, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        grid.Children.Add(texts);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>占满整行宽度的设置块（预览区等）。</summary>
    private FrameworkElement FullRow(string title, string? description, FrameworkElement content)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 9, 0, 9) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = FontMedium,
            FontSize = 13.5,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                FontFamily = FontRegular,
                FontSize = 11.5,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        content.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(content);
        return stack;
    }

    private CheckBox Toggle(bool value, Action<bool> onChange)
    {
        var check = new CheckBox
        {
            IsChecked = value,
            Style = StyleRes("ToggleSwitch"),
            VerticalAlignment = VerticalAlignment.Center
        };
        check.Checked += (_, _) => Guard("修改设置", () => onChange(true));
        check.Unchecked += (_, _) => Guard("修改设置", () => onChange(false));
        return check;
    }

    private Slider Slider(double min, double max, double value, double step, Action<double> onChange, double width = 250)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            Width = width,
            Style = StyleRes("SettingSlider"),
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, e) => Guard("修改设置", () => onChange(e.NewValue));
        return slider;
    }

    private FrameworkElement SliderRow(
        string title,
        string? description,
        double min,
        double max,
        double value,
        double step,
        Func<double, string> format,
        Action<double> onChange,
        double sliderWidth = 250)
    {
        var valueText = new TextBlock
        {
            Text = format(value),
            MinWidth = 64,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = FontMedium,
            FontSize = 13,
            Foreground = BrandBrush
        };
        var slider = Slider(min, max, value, step, v =>
        {
            valueText.Text = format(v);
            onChange(v);
        }, sliderWidth);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        return Row(title, description, panel);
    }

    private FrameworkElement Segmented<T>(IReadOnlyList<(string Label, T Value)> options, T current, Action<T> onChange)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF8)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var radio = new RadioButton
            {
                Content = label,
                Style = StyleRes("SegmentedButton"),
                Tag = value,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                Margin = new Thickness(0)
            };
            radio.Checked += (_, _) =>
            {
                if (radio.Tag is T typed) Guard("修改设置", () => onChange(typed));
            };
            panel.Children.Add(radio);
        }
        border.Child = panel;
        return border;
    }

    private Button ActionButton(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = StyleRes(primary ? "SettingPrimaryButton" : "SettingGhostButton"),
            Margin = new Thickness(0, 0, 10, 0)
        };
        button.Click += (_, _) => Guard(text, onClick);
        return button;
    }

    private FrameworkElement ButtonRow(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private FrameworkElement ColorRow(string title, string? description, string value, string fallback, Action<string> onChange)
    {
        var field = new ColorField(this, value, fallback, normalized => Guard("修改颜色", () => onChange(normalized)));
        return Row(title, description, field);
    }

    /// <summary>颜色 + 透明度 百分比的一行（按钮四态用）。</summary>
    private FrameworkElement ColorWithOpacityRow(
        string title,
        string? description,
        string color,
        string fallback,
        int transparencyPercent,
        Action<string> onColor,
        Action<int> onTransparency)
    {
        var field = new ColorField(this, color, fallback, normalized => Guard("修改按钮颜色", () => onColor(normalized)));
        var valueText = new TextBlock
        {
            Text = $"{transparencyPercent}%",
            Width = 48,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = FontMedium,
            FontSize = 13,
            Foreground = BrandBrush
        };
        var slider = Slider(0, 100, transparencyPercent, 1, v =>
        {
            valueText.Text = $"{(int)Math.Round(v)}%";
            onTransparency((int)Math.Round(v));
        }, 150);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(field);
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        return Row(title, description, panel);
    }

    private static void Guard(string action, Action body)
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"设置窗口「{action}」失败", ex);
            MessageBox.Show($"{action}失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Info(string message) =>
        MessageBox.Show(message, "班机", MessageBoxButton.OK, MessageBoxImage.Information);

    // =========================================================================
    // 按钮样式实时预览（四态）
    // =========================================================================

    private sealed class ButtonStylePreview : WrapPanel
    {
        private readonly ToolButton _idle;
        private readonly ToolButton _hover;
        private readonly ToolButton _pressed;
        private readonly ToolButton _accent;

        public ButtonStylePreview()
        {
            Margin = new Thickness(0, 2, 0, 0);
            _idle = Create("常态", ToolButtonPreviewState.Live);
            _hover = Create("悬浮", ToolButtonPreviewState.Hover);
            _pressed = Create("按下", ToolButtonPreviewState.Pressed);
            _accent = Create("取消计时（强调）", ToolButtonPreviewState.Accent);
            Children.Add(_idle);
            Children.Add(_hover);
            Children.Add(_pressed);
            Children.Add(_accent);
        }

        private static ToolButton Create(string text, ToolButtonPreviewState state)
        {
            return new ToolButton
            {
                Content = text,
                ForcedState = state,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 14, 12),
                MinWidth = 78
            };
        }

        public void Apply(ButtonStyleSettings style)
        {
            ToolButtonStyler.Apply(_idle, style);
            ToolButtonStyler.Apply(_hover, style);
            ToolButtonStyler.Apply(_pressed, style);
            ToolButtonStyler.Apply(_accent, style);

            // 预览按钮：字号跟随设置，但保证内容宽度够用
            foreach (var button in new[] { _idle, _hover, _pressed, _accent })
            {
                button.FontSize = Math.Clamp(style.FontSize, 8, 40);
                button.Padding = new Thickness(16, 7, 16, 7);
            }
        }
    }

    /// <summary>颜色输入框 + 预设调色板弹层。</summary>
    private sealed class ColorField : StackPanel
    {
        private static readonly string[] Palette =
        {
            "#317FFF", "#5998FF", "#0051D6", "#3E6BFF", "#2E69FF", "#1E3A8A",
            "#FF3030", "#F43F5E", "#EC4899", "#D97757", "#E58A6D", "#F59E0B",
            "#FF9F1C", "#22C55E", "#10B981", "#06B6D4", "#8B5CF6", "#6366F1",
            "#231F20", "#1F2329", "#3A4250", "#6B7280", "#9AA3AF", "#C8D2E0",
            "#E5E9F2", "#F5F7FB", "#FFFFFF", "#000000", "#F2C6B4", "#FFF1F1"
        };

        private readonly SettingsWindow _owner;
        private readonly string _fallback;
        private readonly Action<string> _onChange;
        private readonly Border _swatch;
        private readonly TextBox _text;
        private readonly Popup _popup;

        public ColorField(SettingsWindow owner, string value, string fallback, Action<string> onChange)
        {
            _owner = owner;
            _fallback = fallback;
            _onChange = onChange;
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;

            _swatch = new Border
            {
                Width = 34,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = owner.DividerBrush,
                Background = new SolidColorBrush(ToolButtonStyler.ParseColor(value, Colors.Gray)),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "点击选择预设颜色"
            };

            _text = new TextBox
            {
                Text = value,
                Width = 104,
                Style = owner.StyleRes("Input.TextBox"),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "可输入 #RRGGBB 形式的颜色"
            };
            _text.LostFocus += (_, _) => Commit();
            _text.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Commit();
                e.Handled = true;
            };

            _popup = new Popup
            {
                PlacementTarget = _swatch,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = BuildPalette()
            };
            _swatch.MouseLeftButtonUp += (_, _) => _popup.IsOpen = !_popup.IsOpen;

            Children.Add(_swatch);
            Children.Add(_text);
            Children.Add(_popup);
        }

        private UIElement BuildPalette()
        {
            var wrap = new WrapPanel { Width = 8 * 30 };
            foreach (var color in Palette)
            {
                var cell = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = _owner.DividerBrush,
                    Background = new SolidColorBrush(ToolButtonStyler.ParseColor(color, Colors.Gray)),
                    Cursor = Cursors.Hand,
                    ToolTip = color
                };
                var captured = color;
                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _text.Text = captured;
                    Commit();
                    _popup.IsOpen = false;
                };
                wrap.Children.Add(cell);
            }

            return new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                BorderBrush = _owner.DividerBrush,
                Effect = new DropShadowEffect
                {
                    Direction = 270,
                    ShadowDepth = 4,
                    BlurRadius = 14,
                    Opacity = 0.16,
                    RenderingBias = RenderingBias.Performance
                },
                Child = wrap
            };
        }

        private void Commit()
        {
            var normalized = PetStyleSettings.NormalizeHex(_text.Text, _fallback);
            _text.Text = normalized;
            _swatch.Background = new SolidColorBrush(ToolButtonStyler.ParseColor(normalized, Colors.Gray));
            _onChange(normalized);
        }
    }

    // =========================================================================
    // 开机自启同步
    // =========================================================================

    private void SyncAutoStartState()
    {
        if (!AppServices.IsInitialized) return;
        var actual = AutoStartRegistration.IsEnabled();
        if (!actual) return; // 以设置值为准：关闭时不动注册表
        if (!Settings.Current.AutoStartWithWindows)
            Settings.Update(x => x.AutoStartWithWindows = true);
    }

    private void OnAutoStartToggled(bool enable)
    {
        if (!AutoStartRegistration.TryApply(enable, out var error))
        {
            MessageBox.Show(error ?? "设置开机启动失败。", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
            RebuildSection("general");
            return;
        }
        Settings.Update(x => x.AutoStartWithWindows = enable);
    }
}
