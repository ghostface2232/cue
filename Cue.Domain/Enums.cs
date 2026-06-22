namespace Cue.Domain;

/// <summary>Task priority. Maps loosely to Todoist P1–P4 / Things flags.</summary>
public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>How often a recurrence repeats.</summary>
public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// When the next occurrence is computed from.
/// <see cref="FixedSchedule"/> advances on the calendar regardless of completion;
/// <see cref="AfterCompletion"/> schedules the next one relative to when the task is checked off
/// (Things' "repeat after completion").
/// </summary>
public enum RecurrenceMode
{
    FixedSchedule,
    AfterCompletion,
}

/// <summary>How a project's tasks are presented.</summary>
public enum ProjectView
{
    List,
    Board,
}
