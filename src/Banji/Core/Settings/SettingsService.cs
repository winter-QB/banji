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
        MigrateIfNeeded(_current);
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// v1.0 → v1.1 设置迁移（第二轮改版）：
    /// 气泡默认改白底不模糊；宠物默认改方形雾蓝色；下课提示默认改白色。
    /// 只迁移“仍是旧默认值”的字段，用户自定义过的值不覆盖。
    /// </summary>
    private static void MigrateIfNeeded(AppSettings s)
    {
        try
        {
            if (s.Version >= 2) return;

            if (s.Bubble is not null)
            {
                if (s.Bubble.OpacityPercent == 50) s.Bubble.OpacityPercent = 0;
                if (s.Bubble.UseBlur) s.Bubble.UseBlur = false;
            }

            if (s.Pet is not null)
            {
                if (string.Equals(s.Pet.BodyColor, "#D97757", StringComparison.OrdinalIgnoreCase))
                    s.Pet.BodyColor = "#8FB6F2";
                if (string.Equals(s.Pet.BodyShadeColor, "#D97757", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Pet.BodyShadeColor, "#C05F42", StringComparison.OrdinalIgnoreCase))
                    s.Pet.BodyShadeColor = "#6E96DA";
                if (string.Equals(s.Pet.BellyColor, "#E58A6D", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Pet.BellyColor, "#F2C6B4", StringComparison.OrdinalIgnoreCase))
                    s.Pet.BellyColor = "#B7CFF7";
            }

            if (s.Notice is not null
                && string.Equals(s.Notice.ClassEndTextColor, "#1F2329", StringComparison.OrdinalIgnoreCase))
            {
                s.Notice.ClassEndTextColor = "#FFFFFF";
            }

            s.Version = 2;
            s.Normalize();
            SafeJsonStore.Save(AppPaths.SettingsFile, s);
            Log.Info("设置已从 v1.0 迁移到 v1.1（气泡白底/宠物雾蓝/下课白字）");
        }
        catch (Exception ex)
        {
            Log.Error("设置迁移失败（不影响启动）", ex);
        }
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
