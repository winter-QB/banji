namespace Banji.Controls.Pet;

/// <summary>空闲自动隐藏状态机的一次判定结果。</summary>
public enum IdleHideAction
{
    None = 0,
    /// <summary>宠物无互动超时 → 收进屏幕侧边成弧线。</summary>
    CollapsePet = 1,
    /// <summary>课表无操作超时 → 进入折叠态。</summary>
    FoldTimetable = 2
}

/// <summary>
/// 宠物/课表的空闲自动收起状态机（v1.3.0，纯逻辑、时钟可注入，便于单测）。
/// <list type="bullet">
///   <item>宠物：无互动满 <c>PetIdleCollapseMinutes</c> 分钟 → 收进侧边；0 = 关闭；</item>
///   <item>课表：无操作满 <c>TimetableIdleFoldMinutes</c> 分钟 → 折叠态；0 = 关闭；</item>
///   <item>任意交互 <see cref="TouchPet"/> / <see cref="TouchTimetable"/> / <see cref="TouchAll"/> 重置计时；</item>
///   <item>已经收起/折叠后不会重复触发，展开/恢复后才重新计时。</item>
/// </list>
/// </summary>
public sealed class IdleAutoHideLogic
{
    private DateTime _lastPetActivity;
    private DateTime _lastTimetableActivity;

    public IdleAutoHideLogic(DateTime now)
    {
        _lastPetActivity = now;
        _lastTimetableActivity = now;
    }

    public DateTime LastPetActivity => _lastPetActivity;
    public DateTime LastTimetableActivity => _lastTimetableActivity;

    /// <summary>宠物当前是否处于“已收进侧边”状态（由窗口在收起/展开时维护）。</summary>
    public bool PetCollapsed { get; private set; }

    /// <summary>课表当前是否处于“折叠态”。</summary>
    public bool TimetableFolded { get; private set; }

    public void TouchPet(DateTime now)
    {
        _lastPetActivity = now;
        if (PetCollapsed) PetCollapsed = false;
    }

    public void TouchTimetable(DateTime now)
    {
        _lastTimetableActivity = now;
        if (TimetableFolded) TimetableFolded = false;
    }

    public void TouchAll(DateTime now)
    {
        TouchPet(now);
        TouchTimetable(now);
    }

    /// <summary>标记已收起（窗口完成收起动画后调用），避免重复触发并冻结计时。</summary>
    public void MarkPetCollapsed() => PetCollapsed = true;

    public void MarkPetExpanded(DateTime now)
    {
        PetCollapsed = false;
        _lastPetActivity = now;
    }

    public void MarkTimetableFolded() => TimetableFolded = true;

    public void MarkTimetableUnfolded(DateTime now)
    {
        TimetableFolded = false;
        _lastTimetableActivity = now;
    }

    /// <summary>
    /// 判断当前是否该自动收起。一次只返回一个动作（先处理宠物收起，再处理课表折叠），
    /// 调用方执行完动作后再 Evaluate 一次即可拿到另一个。
    /// </summary>
    public IdleHideAction Evaluate(DateTime now, int petIdleMinutes, int timetableIdleMinutes)
    {
        if (!PetCollapsed && petIdleMinutes > 0)
        {
            var idle = now - _lastPetActivity;
            if (idle.TotalMinutes >= petIdleMinutes) return IdleHideAction.CollapsePet;
        }

        if (!TimetableFolded && timetableIdleMinutes > 0)
        {
            var idle = now - _lastTimetableActivity;
            if (idle.TotalMinutes >= timetableIdleMinutes) return IdleHideAction.FoldTimetable;
        }

        return IdleHideAction.None;
    }

    /// <summary>宠物空闲剩余时间（用于测试/调试）；已收起或关闭时返回 null。</summary>
    public TimeSpan? PetIdleRemaining(DateTime now, int petIdleMinutes)
    {
        if (PetCollapsed || petIdleMinutes <= 0) return null;
        var remaining = TimeSpan.FromMinutes(petIdleMinutes) - (now - _lastPetActivity);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>课表空闲剩余时间；已折叠或关闭时返回 null。</summary>
    public TimeSpan? TimetableIdleRemaining(DateTime now, int timetableIdleMinutes)
    {
        if (TimetableFolded || timetableIdleMinutes <= 0) return null;
        var remaining = TimeSpan.FromMinutes(timetableIdleMinutes) - (now - _lastTimetableActivity);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
