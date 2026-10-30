using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Banji.Core.Infrastructure;

namespace Banji.Core.IO;

/// <summary>
/// 原子 JSON 存储：写 .tmp → 备份旧文件 → File.Replace / Move。
/// 读取失败时把坏文件改名保存为 .corrupt-时间戳，并返回默认值。
/// </summary>
public static class SafeJsonStore
{
    private static readonly object Gate = new();

    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return fallback();
            var value = JsonSerializer.Deserialize<T>(json, Options);
            return value ?? fallback();
        }
        catch (Exception ex)
        {
            Log.Error($"反序列化失败: {path}", ex);
            TryQuarantine(path);
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(value, Options);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    var bak = path + ".bak";
                    try
                    {
                        File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                    }
                    catch
                    {
                        File.Copy(tmp, path, overwrite: true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"保存失败: {path}", ex);
            }
        }
    }

    /// <summary>异步防抖保存（用于高频设置更改）。</summary>
    public static IDisposable DebouncedSave<T>(string path, Func<T> valueFactory, TimeSpan delay)
    {
        return new DebouncedSaver<T>(path, valueFactory, delay);
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var target = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, target, overwrite: true);
            Log.Warn($"损坏文件已隔离: {target}");
        }
        catch
        {
            // ignore
        }
    }

    private sealed class DebouncedSaver<T> : IDisposable
    {
        private readonly string _path;
        private readonly Func<T> _factory;
        private readonly TimeSpan _delay;
        private readonly object _gate = new();
        private Timer? _timer;
        private bool _disposed;

        public DebouncedSaver(string path, Func<T> factory, TimeSpan delay)
        {
            _path = path;
            _factory = factory;
            _delay = delay;
        }

        public void Schedule()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _timer?.Dispose();
                _timer = new Timer(_ => Flush(), null, _delay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (_disposed) return;
                Timer? timer = _timer;
                _timer = null;
                timer?.Dispose();
            }
            try
            {
                Save(_path, _factory());
            }
            catch (Exception ex)
            {
                Log.Error("防抖保存失败", ex);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
