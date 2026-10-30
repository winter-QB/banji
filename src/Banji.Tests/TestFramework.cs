using System.Reflection;

namespace Banji.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BanjiTestAttribute : Attribute
{
    public string? Name { get; init; }
    public bool Ignore { get; init; }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault() ?? "";
        var methods = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsClass)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<BanjiTestAttribute>() is not null)
            .Where(m => filter.Length == 0 || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.MetadataToken)
            .ToList();

        if (methods.Count == 0)
        {
            Console.WriteLine("没有找到任何测试。");
            return 2;
        }

        var passed = 0;
        var failed = 0;
        var ignored = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var m in methods)
        {
            var attr = m.GetCustomAttribute<BanjiTestAttribute>()!;
            var label = $"{m.DeclaringType!.Name}.{m.Name}";
            if (attr.Ignore) { ignored++; Console.WriteLine($"SKIP  {label}"); continue; }
            try
            {
                m.Invoke(null, null);
                passed++;
                Console.WriteLine($"PASS  {label}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {label}");
                Console.WriteLine($"      {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {label}");
                Console.WriteLine($"      {ex.Message}");
            }
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"总计 {methods.Count}，通过 {passed}，失败 {failed}，跳过 {ignored}，耗时 {sw.ElapsedMilliseconds}ms");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>极简断言工具（不依赖任何 NuGet 包）。</summary>
public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new Exception(message ?? "期望 true，实际 false");
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "期望 false，实际 true");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message ?? "值不相等"}：期望 [{expected}]，实际 [{actual}]");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) throw new Exception(message ?? "期望非 null，实际 null");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) throw new Exception(message ?? $"期望 null，实际 [{value}]");
    }

    public static void InRange(double value, double min, double max, string? message = null)
    {
        if (value < min || value > max)
            throw new Exception($"{message ?? "值超出范围"}：{value} 不在 [{min}, {max}]");
    }

    public static T Throws<T>(Action action, string? message = null) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new Exception($"{message ?? "异常类型不符"}：期望 {typeof(T).Name}，实际 {ex.GetType().Name}");
        }
        throw new Exception($"{message ?? "期望抛出异常"}：{typeof(T).Name} 但没有抛出");
    }
}
