using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// The one piece of health state that outlives an update's DI scope, and therefore the only place the
/// platform's "three consecutive failures" can actually be counted. Its contract is entirely about
/// lifetime, which the tracker's own tests cannot pin down: they hold one counter for the whole test, so
/// what a <em>new</em> process or a re-enabled bot sees is invisible there.
/// </summary>
public sealed class BotFailureCounterTests
{
    [Fact]
    public void Increment_CountsConsecutiveFailures_PerBot()
    {
        var counter = new BotFailureCounter();

        var counts = (First: counter.Increment(1), Again: counter.Increment(1), Other: counter.Increment(2));

        Assert.Equal((1, 2, 1), counts);
    }

    [Fact]
    public void RecordSuccess_BreaksTheStreak_SoTheNextFailureStartsOver()
    {
        var counter = new BotFailureCounter();
        counter.Increment(1);
        counter.Increment(1);

        counter.RecordSuccess(1);

        Assert.Equal(1, counter.Increment(1));
    }

    [Fact]
    public void RecordSuccess_AsksForAStatusCheck_WhenItBrokeAStreak()
    {
        var counter = new BotFailureCounter();
        counter.Increment(1);

        var needsStatusCheck = counter.RecordSuccess(1);

        Assert.True(needsStatusCheck);
    }

    [Fact]
    public void RecordSuccess_AsksForAStatusCheck_OnTheFirstSuccessOfTheProcess_WithNoStreakAtAll()
    {
        // A bot can be persisted Failing by a process whose counts died with it, so "nothing to clear" is
        // not the same question as "nothing to fix".
        var counter = new BotFailureCounter();

        var needsStatusCheck = counter.RecordSuccess(1);

        Assert.True(needsStatusCheck);
    }

    [Fact]
    public void RecordSuccess_StopsAskingForAStatusCheck_OnceTheBotIsReconciled()
    {
        // Every update a healthy bot handles calls this; answering true each time would put a registry
        // read on the hot path for the whole fleet, forever.
        var counter = new BotFailureCounter();
        counter.RecordSuccess(1);

        var laterSuccesses = (counter.RecordSuccess(1), counter.RecordSuccess(1));

        Assert.Equal((false, false), laterSuccesses);
    }

    [Fact]
    public void RecordSuccess_AsksAgain_AfterAFreshStreak()
    {
        var counter = new BotFailureCounter();
        counter.RecordSuccess(1);
        counter.Increment(1);

        var needsStatusCheck = counter.RecordSuccess(1);

        Assert.True(needsStatusCheck);
    }

    [Fact]
    public void Forget_DropsTheStreak_SoAReEnabledBotStartsClean()
    {
        var counter = new BotFailureCounter();
        counter.Increment(1);
        counter.Increment(1);

        counter.Forget(1);

        Assert.Equal(1, counter.Increment(1));
    }

    [Fact]
    public void Forget_DropsTheReconciledMark_SoAReAddedBotIsCheckedAgain()
    {
        // A bot id is reused only after a remove/re-add, and the row behind the new one is a different bot
        // with its own persisted status.
        var counter = new BotFailureCounter();
        counter.RecordSuccess(1);

        counter.Forget(1);

        Assert.True(counter.RecordSuccess(1));
    }

    [Fact]
    public void Forget_TouchesNoOtherBot()
    {
        var counter = new BotFailureCounter();
        counter.Increment(1);
        counter.Increment(2);
        counter.Increment(2);

        counter.Forget(1);

        Assert.Equal(3, counter.Increment(2));
    }

    [Fact]
    public void Forget_IsSafe_ForABotItHasNeverSeen()
    {
        var counter = new BotFailureCounter();

        counter.Forget(999);

        Assert.Equal(1, counter.Increment(999));
    }

    [Fact]
    public async Task Increment_CountsEveryFailure_WhenUpdatesAreHandledConcurrently()
    {
        // A lost increment is a bot that never reaches the threshold — the platform would simply never
        // report it broken, and nothing downstream could detect that.
        var counter = new BotFailureCounter();

        await Parallel.ForAsync(0, 500, (_, _) =>
        {
            counter.Increment(1);
            return ValueTask.CompletedTask;
        });

        Assert.Equal(501, counter.Increment(1));
    }
}