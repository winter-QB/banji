namespace Banji.Core.Quips;

/// <summary>
/// 台词来源选择器：按设置选择“固定台词库”或“本地离线生成器”，
/// 并保证两种来源混用时相邻两次不会重复（跨来源也去重）。
/// </summary>
public sealed class QuipProvider
{
    private readonly QuipLibrary _library;
    private readonly LocalQuipGenerator _generator;
    private string? _last;

    public QuipProvider()
        : this(null, null)
    {
    }

    public QuipProvider(QuipLibrary? library, LocalQuipGenerator? generator)
    {
        _library = library ?? new QuipLibrary();
        _generator = generator ?? new LocalQuipGenerator();
    }

    /// <summary>最近一次返回的台词。</summary>
    public string? Last => _last;

    /// <param name="useLocalGenerator">true 时使用本地离线生成器（Settings.UseLocalQuipGenerator）。</param>
    public string Next(bool useLocalGenerator)
    {
        var text = "";
        for (var attempt = 0; attempt < 12; attempt++)
        {
            text = useLocalGenerator ? _generator.Next() : _library.Next();
            if (!string.IsNullOrWhiteSpace(text) && text != _last) break;
        }
        if (string.IsNullOrWhiteSpace(text)) text = useLocalGenerator ? _library.Next() : _generator.Next();
        _last = text;
        return text;
    }
}
