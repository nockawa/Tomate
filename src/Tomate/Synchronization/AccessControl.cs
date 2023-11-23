using System.Diagnostics;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 8 bytes of data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct AccessControl
{
    // This type can be used on Memory Mapped File for interprocess synchronization, so the Managed Thread Id is not enough as
    //  a discriminant, we also use the Process Id to store a unique value in _lockedByThreadId
    private static readonly int ProcessId = Process.GetCurrentProcess().Id;

    /// <summary>
    /// Constructor an instance stored in the given memory segment
    /// </summary>
    /// <param name="segment">The memory segment where the instance will be stored.</param>
    /// <returns>The ref to the instance</returns>
    public static ref AccessControl Construct(MemorySegment<AccessControl> segment)
    {
        ref var ac = ref segment.AsRef();
        ac._lockedByThreadId  = 0;
        ac._sharedUsedCounter = 0;
        return ref ac;
    }

    /// <summary>
    /// Reset the usage of the instance to its default state.
    /// </summary>
    public void Reset()
    {
        _lockedByThreadId = 0;
        _sharedUsedCounter = 0;
    }

    // Don't change this data layout as there is a C++ counterpart of AccessControl and we can use this type with IPC
    private volatile int _lockedByThreadId;
    private volatile int _sharedUsedCounter;

    /// <summary>
    /// Returns the Id of the current Process/Thread that own the lock 
    /// </summary>
    /// <remarks>
    /// <c>0</c> is returned if the Access Control is not locked.
    /// The returned value is a xor combination of the Process Id and the threadId and should be used for information purpose only.
    /// </remarks>
    public int LockedById => _lockedByThreadId;
    
    /// <summary>
    /// Returns the count of concurrent accesses.
    /// </summary>
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
        var ct = CurrentProcessThreadId;

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
        var ct = CurrentProcessThreadId;

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
    private static int CurrentProcessThreadId => Environment.CurrentManagedThreadId ^ ProcessId;
}