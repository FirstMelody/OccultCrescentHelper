namespace BOCCHI.Modules.Automator;

public enum ActivityState
{
    Idle,
    Pathfinding,
    WaitingToStartCriticalEncounter,
    Participating,
    Done,
}

public static class ActivityStateExtensions
{
    public static string ToLabel(this ActivityState state)
    {
        return state switch
        {
            ActivityState.Idle => "待机",
            ActivityState.Pathfinding => "正在前往目标",
            ActivityState.WaitingToStartCriticalEncounter => "等待紧急遭遇战开始",
            ActivityState.Participating => "正在参战",
            ActivityState.Done => "已完成",
            _ => "未知状态",
        };
    }
}
