namespace Tomate;

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