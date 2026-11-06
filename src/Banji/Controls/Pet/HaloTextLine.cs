using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Banji.Controls.Pet;

/// <summary>一行文字里的一个样式片段（同一行可混排不同字号/颜色/字重）。</summary>
public readonly record struct HaloSegment(string Text, FontFamily Font, double FontSize, Brush Brush, FontWeight? Weight = null);

/// <summary>
/// 纯文字悬浮层（第二轮用户要求：课表/信息框去掉卡片背景，文字周围重高斯模糊压暗背景）：
/// 由下到上是 —— ① 远层大半径黑色高斯模糊（压暗背景）② 四个方向平移的黑色描边层（保证纯白/纯黑背景上都清晰）
/// ③ 白色/彩色文字层。没有任何卡片边框。
/// <para>本控件不参与命中测试（空白/文字区域不拦截鼠标）。</para>
/// </summary>
public sealed class HaloTextLine : Grid
{
    private const double WideHaloRadius = 20;
    private const double WideHaloOpacity = 0.66;
    private const double OutlineOffset = 0.65;

    private static readonly (double Dx, double Dy)[] OutlineOffsets =
    {
        (-OutlineOffset, -OutlineOffset),
        (OutlineOffset, -OutlineOffset),
        (-OutlineOffset, OutlineOffset),
        (OutlineOffset, OutlineOffset)
    };

    private readonly TextBlock _haloWide;
    private readonly List<TextBlock> _outlines = new();
    private readonly TextBlock _front;
    private readonly BlurEffect _wideEffect;

    public HaloTextLine()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _wideEffect = new BlurEffect { Radius = WideHaloRadius, RenderingBias = RenderingBias.Quality };
        _haloWide = CreateHalo(_wideEffect, WideHaloOpacity);
        Children.Add(_haloWide);

        foreach (var (dx, dy) in OutlineOffsets)
        {
            var outline = CreateHalo(null, 1.0);
            outline.RenderTransform = new TranslateTransform(dx, dy);
            _outlines.Add(outline);
            Children.Add(outline);
        }

        _front = CreateFront();
        Children.Add(_front);
    }

    private static TextBlock CreateHalo(BlurEffect? effect, double opacity) => new()
    {
        Foreground = Brushes.Black,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Effect = effect,
        Opacity = opacity
    };

    private static TextBlock CreateFront() => new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    /// <summary>远层光晕的高斯模糊半径（建议 14~22，越大背景压得越平整）。</summary>
    public double HaloRadius
    {
        get => _wideEffect.Radius;
        set
        {
            if (double.IsNaN(value) || value <= 0) return;
            _wideEffect.Radius = Math.Clamp(value, 8, 40);
        }
    }

    public void SetText(string? text, FontFamily font, double fontSize, Brush brush)
        => SetSegments(new[] { new HaloSegment(text ?? "", font, fontSize, brush) });

    public void SetSegments(IReadOnlyList<HaloSegment> segments)
    {
        _front.Inlines.Clear();
        _haloWide.Inlines.Clear();
        foreach (var outline in _outlines) outline.Inlines.Clear();
        if (segments is null || segments.Count == 0) return;

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text)) continue;
            var weight = segment.Weight ?? FontWeights.Normal;

            _front.Inlines.Add(new Run(segment.Text)
            {
                FontFamily = segment.Font,
                FontSize = segment.FontSize,
                FontWeight = weight,
                Foreground = segment.Brush
            });

            _haloWide.Inlines.Add(new Run(segment.Text)
            {
                FontFamily = segment.Font,
                FontSize = segment.FontSize,
                FontWeight = weight
            });

            foreach (var outline in _outlines)
            {
                outline.Inlines.Add(new Run(segment.Text)
                {
                    FontFamily = segment.Font,
                    FontSize = segment.FontSize,
                    FontWeight = weight
                });
            }
        }
    }

    public void Clear()
    {
        _front.Inlines.Clear();
        _haloWide.Inlines.Clear();
        foreach (var outline in _outlines) outline.Inlines.Clear();
    }
}

