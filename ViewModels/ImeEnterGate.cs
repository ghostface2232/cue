namespace Cue.ViewModels;

/// <summary>What the quick-add box does with an Enter key press.</summary>
public enum ImeEnterAction
{
    /// <summary>Let the keystroke through: the IME is using it to finish the syllable. The line commits when
    /// the composition ends.</summary>
    PassToIme,

    /// <summary>Commit the line now.</summary>
    Commit,

    /// <summary>Consume the keystroke without committing: it is the same physical press that already
    /// committed the line when the composition ended.</summary>
    Swallow,
}

/// <summary>
/// Decides when Enter commits the quick-add line, so one press is enough even while a Korean syllable is
/// still being composed. Almost every Korean line ends mid-composition, so treating that press as "finish the
/// syllable only" would mean pressing Enter twice for nearly every task.
/// </summary>
/// <remarks>
/// IMEs differ in how they deliver that press: the key may arrive before the composition ends, after it, or
/// not at all. All three orders commit exactly once:
/// <list type="bullet">
/// <item>Before: <see cref="EnterPressed"/> sees the composition and marks a commit as pending.</item>
/// <item>Not at all: <see cref="CompositionEnded"/> is told Enter is physically down and marks it itself.</item>
/// <item>After: <see cref="EnterPressed"/> commits and clears the pending mark, or is swallowed if the
/// pending commit already ran.</item>
/// </list>
/// The commit is deferred past <see cref="CompositionEnded"/> so the finished syllable has reached the document
/// by the time the line is read. A composition ended any other way (space, an arrow key, a click) commits nothing.
/// </remarks>
public sealed class ImeEnterGate
{
    // Long enough to cover a late key event for the same press, short enough that no one can deliberately
    // press Enter again within it.
    private const long SamePressWindowMilliseconds = 250;

    private bool _commitPending;
    private long? _committedFromCompositionAt;

    /// <summary>Whether a composition is in flight. While true the line must not be reformatted.</summary>
    public bool IsComposing { get; private set; }

    public void CompositionStarted()
    {
        IsComposing = true;
        _commitPending = false;
    }

    public ImeEnterAction EnterPressed(long nowMilliseconds)
    {
        if (IsComposing)
        {
            _commitPending = true;
            return ImeEnterAction.PassToIme;
        }

        if (_committedFromCompositionAt is { } committedAt
            && nowMilliseconds - committedAt < SamePressWindowMilliseconds)
        {
            return ImeEnterAction.Swallow;
        }

        _commitPending = false;
        return ImeEnterAction.Commit;
    }

    /// <summary>Records the end of a composition. Returns whether a commit is now pending, in which case the
    /// caller schedules <see cref="TryTakePendingCommit"/> to run once the document has updated.</summary>
    public bool CompositionEnded(bool enterIsDown)
    {
        IsComposing = false;
        _commitPending |= enterIsDown;
        return _commitPending;
    }

    /// <summary>Claims the pending commit. False when a key press already committed the line, or a new
    /// composition started in the meantime.</summary>
    public bool TryTakePendingCommit(long nowMilliseconds)
    {
        if (!_commitPending || IsComposing)
            return false;

        _commitPending = false;
        _committedFromCompositionAt = nowMilliseconds;
        return true;
    }
}
