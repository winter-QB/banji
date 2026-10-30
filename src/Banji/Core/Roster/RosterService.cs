using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Rosters;

/// <summary>占位实现，由 core-roster 负责整体替换。</summary>
public sealed class RosterService : IRosterService
{
    private List<Roster> _rosters = new();

    public RosterService()
    {
        _rosters = SafeJsonStore.Load(AppPaths.RostersFile, () => new List<Roster>());
    }

    public IReadOnlyList<Roster> Rosters => _rosters;
    public event EventHandler? Changed;

    public Roster? Get(string? rosterId)
        => string.IsNullOrEmpty(rosterId) ? null : _rosters.FirstOrDefault(r => r.Id == rosterId);

    public Roster? Active => Get(Banji.Core.Boot.AppServices.Current.Settings.Current.ActiveRosterId) ?? _rosters.FirstOrDefault();

    public void SetActive(string? rosterId)
    {
        Banji.Core.Boot.AppServices.Current.Settings.Update(s => s.ActiveRosterId = rosterId ?? "");
    }

    public Roster Add(Roster roster)
    {
        _rosters.Add(roster);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return roster;
    }

    public void Update(Roster roster)
    {
        var idx = _rosters.FindIndex(r => r.Id == roster.Id);
        if (idx >= 0) _rosters[idx] = roster;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string rosterId)
    {
        _rosters.RemoveAll(r => r.Id == rosterId);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Roster CreateManual(string name, IEnumerable<Student> students, string? subject = null)
        => Add(new Roster
        {
            Name = name,
            Subject = subject,
            Students = students.Where(s => s.IsValid).ToList()
        });

    public void Save() => SafeJsonStore.Save(AppPaths.RostersFile, _rosters);

    public RosterImportPreview PreviewImport(string filePath)
        => throw new NotSupportedException("花名册导入待实现");

    public Roster Import(string filePath, RosterImportOptions options)
        => throw new NotSupportedException("花名册导入待实现");
}
