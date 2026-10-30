using System.Windows;
using System.Windows.Media;

namespace Banji.Core.Infrastructure;

/// <summary>字体自检：确认 6 个鸿蒙字体资源都真正加载（而不是悄悄回退到系统字体）。</summary>
public static class FontCheck
{
    public static string Run()
    {
        var lines = new List<string>();
        var keys = new[] { "Font.Thin", "Font.Light", "Font.Regular", "Font.Medium", "Font.Bold", "Font.Black" };
        var allOk = true;
        foreach (var key in keys)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is not FontFamily family)
                {
                    lines.Add($"{key}: MISSING_RESOURCE");
                    allOk = false;
                    continue;
                }

                var familyNames = string.Join(" / ", family.FamilyNames.Values);
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ok = typeface.TryGetGlyphTypeface(out var glyph);
                var uri = ok && glyph is not null ? glyph.FontUri.ToString() : "-";
                lines.Add($"{key}: ok={ok} family=[{familyNames}] uri={uri}");
                if (!ok) allOk = false;
            }
            catch (Exception ex)
            {
                lines.Add($"{key}: ERROR {ex.GetType().Name}: {ex.Message}");
                allOk = false;
            }
        }

        // 诊断：资源是否存在 + 指定文件 URI 形式
        lines.Add("---- diagnostics ----");
        var sample = "pack://application:,,,/Assets/Fonts/HarmonyOS_Sans_SC_Regular.ttf";
        try
        {
            var s = Application.GetResourceStream(new Uri(sample));
            lines.Add($"resource {sample}: {(s is null ? "NULL" : s.Stream.Length + " bytes")}");
        }
        catch (Exception ex)
        {
            lines.Add($"resource {sample}: ERROR {ex.GetType().Name}: {ex.Message}");
        }

        var candidates = new[]
        {
            "pack://application:,,,/Assets/Fonts/#HarmonyOS Sans SC",
            "pack://application:,,,/Assets/Fonts/#HarmonyOS Sans SC Regular",
            "pack://application:,,,/Assets/Fonts/HarmonyOS_Sans_SC_Regular.ttf#HarmonyOS Sans SC",
            "pack://application:,,,/Banji;component/Assets/Fonts/#HarmonyOS Sans SC",
            "pack://application:,,,/Banji;component/Assets/Fonts/HarmonyOS_Sans_SC_Regular.ttf#HarmonyOS Sans SC",
            "pack://application:,,,/Assets/Fonts/#HarmonyOS Sans SC Thin",
            "pack://application:,,,/Assets/Fonts/#HarmonyOS Sans SC Medium"
        };
        foreach (var candidate in candidates)
        {
            try
            {
                var ff = new FontFamily(candidate);
                var names = string.Join(" / ", ff.FamilyNames.Values);
                var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ok = tf.TryGetGlyphTypeface(out var g);
                lines.Add($"cand {candidate} => ok={ok} names=[{names}] uri={(g is null ? "-" : g.FontUri.ToString())}");
            }
            catch (Exception ex)
            {
                lines.Add($"cand {candidate} => ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            var families = Fonts.GetFontFamilies(new Uri("pack://application:,,,/Assets/Fonts/"));
            lines.Add("folder families: " + string.Join(" | ", families.Select(f => string.Join("/", f.FamilyNames.Values))));
        }
        catch (Exception ex)
        {
            lines.Add($"folder families ERROR: {ex.GetType().Name}: {ex.Message}");
        }

        var summary = $"字体自检: {(allOk ? "全部通过" : "存在问题")}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        Log.Info(summary);
        return summary;
    }
}
