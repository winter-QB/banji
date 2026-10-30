using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Picker;

/// <summary>占位实现，由 core-roster 负责整体替换（含公平约束算法）。</summary>
public sealed class PickService : IPickService
{
    private readonly IRosterService _rosters;
    private readonly ISettingsService _settings;
    private readonly Random _random = new();

    public PickService(IRosterService rosters, ISettingsService settings)
    {
        _rosters = rosters;
        _settings = settings;
    }

    public IReadOnlyList<PickRecord> RecentPicks => Array.Empty<PickRecord>();
    public event EventHandler<PickResult>? Picked;

    public TimeSpan CurrentWindow => TimeSpan.FromMinutes(_settings.Current.ClassDurationMinutes);

    public PickResult Pick(string? rosterId = null)
    {
        var roster = rosterId is null ? _rosters.Active : _rosters.Get(rosterId);
        if (roster is null || roster.Students.Count == 0)
            throw new InvalidOperationException("还没有可抽签的花名册，请先在“花名册管理”中添加。");
        var student = roster.Students[_random.Next(roster.Students.Count)];
        var result = new PickResult
        {
            Roster = roster,
            Student = student,
            DisplayMode = roster.DisplayModeOverride ?? _settings.Current.PickDisplay,
            PickedAt = DateTime.Now,
            SessionPickIndex = 1
        };
        Picked?.Invoke(this, result);
        return result;
    }

    public void ResetSession() { }
}
