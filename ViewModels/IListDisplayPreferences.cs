namespace Cue.ViewModels;

/// <summary>
/// The slice of user display preferences the task list reads. The ViewModels layer is a standalone project
/// below the app, so it can't see the app's WinUI-backed <c>AppPreferences</c> directly — the app implements
/// this interface over it and injects it, the same top-down seam <c>IDateParser</c> uses for the parser's
/// preferences. A null injection (e.g. in unit tests) means "use the defaults".
/// </summary>
public interface IListDisplayPreferences
{
    /// <summary>When true, a task completed today stays dimmed in its place on the active list until the
    /// local day rolls over, instead of dropping into its "완료한 일" section the moment it is completed.
    /// Off (the default) keeps the open-only behavior — a completion leaves the live list at once.</summary>
    bool KeepCompletedForToday { get; }

    /// <summary>When true, a dated task's list row appends its ISO-8601 week number to the schedule line
    /// (e.g. "7월 1일 (수) · W27"), and the quick-add parser recognizes week expressions ("W27", "27주차")
    /// as dates. Off (the default) shows no week number and leaves those expressions in the title.</summary>
    bool ShowWeekNumber { get; }
}
