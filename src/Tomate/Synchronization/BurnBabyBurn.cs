using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Put the calling thread in hold for a given time span
/// </summary>
/// <remarks>
/// This type is mostly for testing/debugging purpose, the calling thread is spinning for a given time span, CPU is not consumed during this span, it relies
/// on a particular assembly instruction that "waits doing nothing".
/// The user typically create a while loop with <see cref="Wait"/> being call as the while predicate.
/// </remarks>
[PublicAPI]
public readonly struct BurnBabyBurn
{
    private readonly DateTime _waitUntil;

    public BurnBabyBurn(TimeSpan? waitSpan)
    {
        _waitUntil = (waitSpan != null) ? (DateTime.UtcNow + waitSpan.Value) : DateTime.MaxValue;
    }

    /// <summary>
    /// Wait a bit
    /// </summary>
    /// <returns><c>true</c> if the wait limit is not reached. <c>false</c> if the wait limit is reached and we should no longer call this method.</returns>
    public bool Wait()
    {
        // Note: current implementation is plain dumb, but should be improved in the future
        if (DateTime.UtcNow < _waitUntil)
        {
            Thread.SpinWait(1);
            return true;
        }

        return false;
    }
}