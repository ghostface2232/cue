namespace Cue.Domain;

/// <summary>
/// Task priority, Todoist-style. <see cref="P1"/> is the most urgent. <see cref="None"/> is the
/// default (no priority flag).
/// </summary>
public enum Priority
{
    None = 0,
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4,
}

/// <summary>How a task group's tasks are presented.</summary>
public enum TaskGroupView
{
    List,
    Board,
}
