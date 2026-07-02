namespace Cue.Domain;

/// <summary>When the single notification for a timed task should fire.</summary>
/// <remarks>
/// <see cref="AtTime"/> is zero so a legacy JSON record with no reminder field deserializes to an
/// at-time reminder without a data migration.
/// </remarks>
public enum ReminderTiming
{
    /// <summary>At the exact wall-clock time stored in the task's <see cref="TaskItem.When"/>.</summary>
    AtTime = 0,

    /// <summary>Ten minutes before the task time.</summary>
    TenMinutesBefore,

    /// <summary>One hour before the task time.</summary>
    OneHourBefore,

    /// <summary>Exactly 24 hours before the task time.</summary>
    OneDayBefore,

    /// <summary>Do not notify for this task.</summary>
    None,
}
