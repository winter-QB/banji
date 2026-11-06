using Banji.Core.Models;

namespace Banji.Core.Abstractions;

// ============================================================================
// 第二轮新增服务契约（Lead 冻结）
// ============================================================================

/// <summary>随机分组：把当前花名册的学生随机/均衡分成若干组。</summary>
public interface IGroupingService
{
    /// <summary>按选项分组。rosterId 为空时使用当前激活花名册。</summary>
    GroupingResult Group(GroupingOptions options, string? rosterId = null);
}

/// <summary>宠物陪伴养成（本地统计 + 成就）。</summary>
public interface IPetGrowthService
{
    PetGrowthStats Stats { get; }
    IReadOnlyList<Achievement> Achievements { get; }
    event EventHandler? Changed;

    /// <summary>应用启动一次（同一天只累计一次天数）。</summary>
    void RecordSessionStart();
    void RecordPat();
    void RecordPick();
    void RecordGroup();
    void RecordNotice();
    void Reset();
}

/// <summary>一键备份/恢复全部本地数据（花名册、课表、设置、抽签历史、养成）。</summary>
public interface IBackupService
{
    /// <summary>导出为 .banji 备份文件（本质是 zip）。返回文件大小（字节）。</summary>
    long ExportTo(string filePath);
    /// <summary>读取备份包信息而不恢复。</summary>
    BackupInfo Inspect(string filePath);
    /// <summary>用备份包覆盖本地数据文件（恢复后需重启应用生效）。</summary>
    void RestoreFrom(string filePath);
    /// <summary>最近一次错误（供 UI 中文提示）。</summary>
    string? LastError { get; }
}
