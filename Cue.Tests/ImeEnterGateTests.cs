using Cue.ViewModels;

namespace Cue.Tests;

public class ImeEnterGateTests
{
    [Fact]
    public void Enter_WithNoComposition_Commits()
    {
        var gate = new ImeEnterGate();

        Assert.Equal(ImeEnterAction.Commit, gate.EnterPressed(0));
    }

    [Fact]
    public void Enter_ArrivingDuringComposition_CommitsOnceWhenItEnds()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();

        Assert.Equal(ImeEnterAction.PassToIme, gate.EnterPressed(0));
        Assert.True(gate.CompositionEnded(enterIsDown: true));
        Assert.True(gate.TryTakePendingCommit(1));
        Assert.False(gate.TryTakePendingCommit(2));
    }

    [Fact]
    public void Enter_NeverDeliveredAsAKey_StillCommitsFromCompositionEnd()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();

        Assert.True(gate.CompositionEnded(enterIsDown: true));
        Assert.True(gate.TryTakePendingCommit(1));
    }

    [Fact]
    public void Enter_ArrivingAfterCompositionEnd_BeforeTheDeferredCommit_CommitsOnce()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();
        gate.CompositionEnded(enterIsDown: true);

        Assert.Equal(ImeEnterAction.Commit, gate.EnterPressed(1));
        Assert.False(gate.TryTakePendingCommit(2));
    }

    [Fact]
    public void Enter_ArrivingAfterTheDeferredCommit_IsTheSamePress_AndSwallowed()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();
        gate.CompositionEnded(enterIsDown: true);
        Assert.True(gate.TryTakePendingCommit(100));

        Assert.Equal(ImeEnterAction.Swallow, gate.EnterPressed(150));
    }

    [Fact]
    public void Enter_PressedDeliberatelyAfterACommit_CommitsAgain()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();
        gate.CompositionEnded(enterIsDown: true);
        Assert.True(gate.TryTakePendingCommit(100));

        Assert.Equal(ImeEnterAction.Commit, gate.EnterPressed(1000));
    }

    [Fact]
    public void CompositionEndedWithoutEnter_DoesNotCommit()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();

        Assert.False(gate.CompositionEnded(enterIsDown: false));
        Assert.False(gate.TryTakePendingCommit(1));
    }

    [Fact]
    public void NewCompositionBeforeTheDeferredCommit_CancelsIt()
    {
        var gate = new ImeEnterGate();
        gate.CompositionStarted();
        gate.CompositionEnded(enterIsDown: true);

        gate.CompositionStarted();

        Assert.False(gate.TryTakePendingCommit(1));
        Assert.True(gate.IsComposing);
    }
}
