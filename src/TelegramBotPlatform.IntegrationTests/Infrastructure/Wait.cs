using System.Diagnostics;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Waits for an asynchronous effect to become observable.
/// <para>
/// The platform has exactly one such seam: a webhook POST is answered as soon as the update is published,
/// and the behavior runs afterwards off the in-memory bus. Waiting on the <em>condition</em> rather than
/// on a duration is what keeps that deterministic — a passing test returns as soon as the effect lands and
/// never sleeps out the budget, and a failing one says what it was waiting for instead of timing out
/// anonymously. The budget is generous because it is only ever paid by a test that was going to fail.
/// </para>
/// </summary>
internal static class Wait
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(5);

    /// <param name="expectation">
    /// Built only on failure, so it can describe the state the test actually ended up observing.
    /// </param>
    public static Task Until(Func<bool> condition, Func<string> expectation, CancellationToken cancellationToken) =>
        Until(() => Task.FromResult(condition()), expectation, cancellationToken);

    /// <summary>For a condition that has to be asked over HTTP — a status the admin API reports, say.</summary>
    public static async Task Until(
        Func<Task<bool>> condition, Func<string> expectation, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();

        while (!await condition())
        {
            if (elapsed.Elapsed >= _budget)
            {
                Assert.Fail($"Timed out after {_budget.TotalSeconds:0.#}s waiting for {expectation()}.");
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }
}