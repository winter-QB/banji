using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Settings;

namespace Banji.Core.Settings;

/// <summary>设置服务：JSON 持久化 + 防抖保存 + Changed 事件。</summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private AppSettings _current;
    private bool _disposed;

    public SettingsService()
    {
        _current = SafeJsonStore.Load(AppPaths.SettingsFile, () => new AppSettings());
        _current.Normalize();
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        if (mutate is null) return;
        lock (_gate)
        {
            mutate(_current);
            _current.Normalize();
            _saveTimer.Change(400, Timeout.Infinite);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = _current.Clone();
        }
        SafeJsonStore.Save(AppPaths.SettingsFile, snapshot);
    }

    public void ResetToDefaults()
    {
        lock (_gate)
        {
            _current = new AppSettings();
            _current.Normalize();
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _saveTimer.Dispose();
    }
}
