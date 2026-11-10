using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Banji.Controls.Pet;

/// <summary>
/// 多行“纯文字悬浮”块：远层模糊压暗 + 四向黑色描边层 + 最上面彩色文字层，
/// 所有光晕/描边层都画在文字层下面，因此相邻行之间不会互相压暗；
/// 在深色壁纸与纯白 PPT 上都能读清。整块不参与命中测试（空白处不拦截鼠标）。
/// </summary>
public sealed class HaloTextStack : Grid
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

    private readonly StackPanel _wideStack;
    private readonly StackPanel[] _outlineStacks;
    private readonly StackPanel _frontStack;
    private readonly List<BlurEffect> _wideEffects = new();
    private double _haloRadius = WideHaloRadius;
    private double _lineSpacing = 1;
    private bool _haloEnabled = true;

    public HaloTextStack()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _wideStack = new StackPanel { IsHitTestVisible = false };
        Children.Add(_wideStack);

        _outlineStacks = new StackPanel[OutlineOffsets.Length];
        for (var i = 0; i < OutlineOffsets.Length; i++)
        {
            var stack = new StackPanel
            {
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(OutlineOffsets[i].Dx, OutlineOffsets[i].Dy)
            };
            _outlineStacks[i] = stack;
            Children.Add(stack);
        }

        _frontStack = new StackPanel { IsHitTestVisible = false };
        Children.Add(_frontStack);
    }

    /// <summary>
    /// v1.5.0：白底卡片上改用深色文字时关闭光晕/描边层（只保留正文字）。
    /// </summary>
    public bool HaloEnabled
    {
        get => _haloEnabled;
        set
        {
            _haloEnabled = value;
            _wideStack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            foreach (var stack in _outlineStacks) stack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>远层光晕高斯模糊半径（建议 14~22）。</summary>
    public double HaloRadius
    {
        get => _haloRadius;
        set
        {
            if (double.IsNaN(value) || value <= 0) return;
            _haloRadius = Math.Clamp(value, 8, 40);
            foreach (var effect in _wideEffects) effect.Radius = _haloRadius;
        }
    }

    /// <summary>行间距（额外像素，默认 1）。</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set
        {
            _lineSpacing = Math.Clamp(value, 0, 20);
            ApplyMargins();
        }
    }

    /// <summary>行高统一值（0 = 用字体自然行高）。设置后每行高度一致，适合课表这种表格化内容。</summary>
    public double RowHeight { get; set; }

    /// <summary>整块不透明度（0~1，默认 1）。</summary>
    public double TextOpacity
    {
        get => _frontStack.Opacity;
        set => _frontStack.Opacity = Math.Clamp(value, 0, 1);
    }

    public void SetLines(IReadOnlyList<IReadOnlyList<HaloSegment>>? lines)
    {
        _wideStack.Children.Clear();
        foreach (var stack in _outlineStacks) stack.Children.Clear();
        _frontStack.Children.Clear();
        _wideEffects.Clear();
        if (lines is null || lines.Count == 0) return;

        foreach (var segments in lines)
        {
            var wideEffect = new BlurEffect { Radius = _haloRadius, RenderingBias = RenderingBias.Quality };
            _wideEffects.Add(wideEffect);

            _wideStack.Children.Add(BuildText(segments, wideEffect, WideHaloOpacity));
            foreach (var stack in _outlineStacks) stack.Children.Add(BuildText(segments, null, 1.0));
            _frontStack.Children.Add(BuildFront(segments));
        }
        ApplyMargins();
    }

    public void Clear() => SetLines(null);

    private static TextBlock BuildText(IReadOnlyList<HaloSegment> segments, BlurEffect? effect, double opacity)
    {
        var block = new TextBlock
        {
            Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Effect = effect,
            Opacity = opacity
        };
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text)) continue;
            block.Inlines.Add(new Run(segment.Text)
            {
                FontFamily = segment.Font,
                FontSize = segment.FontSize,
                FontWeight = segment.Weight ?? FontWeights.Normal
            });
        }
        return block;
    }

    private static TextBlock BuildFront(IReadOnlyList<HaloSegment> segments)
    {
        var block = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text)) continue;
            block.Inlines.Add(new Run(segment.Text)
            {
                FontFamily = segment.Font,
                FontSize = segment.FontSize,
                FontWeight = segment.Weight ?? FontWeights.Normal,
                Foreground = segment.Brush
            });
        }
        return block;
    }

    private void ApplyMargins()
    {
        var count = _frontStack.Children.Count;
        for (var i = 0; i < count; i++)
        {
            var bottom = i == count - 1 ? 0 : _lineSpacing;
            ApplyRowStyle(_wideStack.Children[i] as TextBlock, bottom);
            foreach (var stack in _outlineStacks) ApplyRowStyle(stack.Children[i] as TextBlock, bottom);
            ApplyRowStyle(_frontStack.Children[i] as TextBlock, bottom);
        }
    }

    private void ApplyRowStyle(TextBlock? block, double bottomMargin)
    {
        if (block is null) return;
        block.Margin = new Thickness(0, 0, 0, bottomMargin);
        if (RowHeight > 0) block.Height = RowHeight;
    }
}


