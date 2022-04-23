using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 8 bytes of data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AccessControl
{
    // This type can be used on Memory Mapped File for interprocess synchronization, so the Managed Thread Id is not enough as
    //  a discriminant, we also use the Process Id to store a unique value in _lockedByThreadId
    private static readonly int ProcessId = Process.GetCurrentProcess().Id;

    public static ref AccessControl Construct(MemorySegment<AccessControl> segment)
    {
        ref var ac = ref segment.AsRef();
        ac._lockedByThreadId  = 0;
        ac._sharedUsedCounter = 0;
        return ref ac;
    }

    public void Reset()
    {
        _lockedByThreadId = 0;
        _sharedUsedCounter = 0;
    }

    // Don't change this data layout as there is a C++ counterpart of AccessControl and we can use this type with IPC
    private volatile int _lockedByThreadId;
    private volatile int _sharedUsedCounter;

    public int LockedByThreadId => _lockedByThreadId;
    public int SharedUsedCounter => _sharedUsedCounter;

    public void EnterSharedAccess()
    {
        // Currently exclusively locked, wait it's over
        if (_lockedByThreadId != 0)
        {
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
        }

        // Increment shared usage
        Interlocked.Increment(ref _sharedUsedCounter);

        // Double check on exclusive, in a loop because we need to restore the shared counter to prevent deadlock
        // So we loop until we meet the criteria
        while (_lockedByThreadId != 0)
        {
            Interlocked.Decrement(ref _sharedUsedCounter);
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
            Interlocked.Increment(ref _sharedUsedCounter);
        }
    }

    public void ExitSharedAccess() => Interlocked.Decrement(ref _sharedUsedCounter);

    public void EnterExclusiveAccess()
    {
        var ct = Thread.CurrentThread.ManagedThreadId ^ ProcessId;

        // Fast path: exclusive lock works immediately
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) == 0)
        {
            // No shared use: we're good to go
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared use is over
            var sw = new SpinWait();
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }

            return;
        }

        // Slow path: wait the shared concurrent use is over
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
            {
                sw.SpinOnce();
            }

            // Exit if there's no shared access neither
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared access to be over
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    public bool TryPromoteToExclusiveAccess()
    {
        var ct = Thread.CurrentThread.ManagedThreadId ^ ProcessId;

        // We can enter only if we are the only user (_sharedUsedCounter == 1)
        if (_sharedUsedCounter != 1)
        {
            return false;
        }

        // Try to exclusively lock
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
        {
            return false;
        }

        // Double check now we're locked that we're still the only shared user
        if (_sharedUsedCounter != 1)
        {
            // Another concurrent user came at the same time, remove exclusive access and quit with failure
            _lockedByThreadId = 0;
            return false;
        }

        return true;
    }

    public void DemoteFromExclusiveAccess() => _lockedByThreadId = 0;

    public void ExitExclusiveAccess() => _lockedByThreadId = 0;
}
