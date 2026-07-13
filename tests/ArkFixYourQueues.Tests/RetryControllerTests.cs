using ArkFixYourQueues;

namespace ArkFixYourQueues.Tests;

public sealed class RetryControllerTests
{
    [Fact]
    public void RetryIntervalCannotGoBelowTwoSeconds()
    {
        var controller = new RetryController();
        controller.Start(1, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, controller.RetrySeconds);
    }

    [Fact]
    public void LoadingEvidencePausesRetries()
    {
        var now = DateTimeOffset.UnixEpoch;
        var controller = new RetryController();
        controller.Start(10, now);
        controller.FinishArming(now);
        controller.Attempted(now);
        controller.ObserveScreen(false, now.AddSeconds(2));
        controller.ObserveScreen(false, now.AddSeconds(3));
        Assert.Equal(RetryState.PausedForLoading, controller.State);
        Assert.False(controller.ShouldAttempt(now.AddMinutes(1), true));
    }

    [Fact]
    public void ThreeBaselineFramesResumeAfterReturnToMenu()
    {
        var now = DateTimeOffset.UnixEpoch;
        var controller = new RetryController();
        controller.Start(10, now);
        controller.FinishArming(now);
        controller.Attempted(now);
        controller.ObserveScreen(false, now);
        controller.ObserveScreen(false, now);
        controller.ObserveScreen(true, now);
        controller.ObserveScreen(true, now);
        controller.ObserveScreen(true, now);
        Assert.Equal(RetryState.Ready, controller.State);
    }
}
