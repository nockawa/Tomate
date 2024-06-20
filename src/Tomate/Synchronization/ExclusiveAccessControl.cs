using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Simple access control supporting a single process and only exclusive access
/// </summary>
/// <remarks>
/// The size of this struct is only 4 bytes.
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct ExclusiveAccessControl
{
    #region Public APIs

    #region Methods

    /// <summary>
    /// Release a previously gained exclusive access by the calling thread 
    /// </summary>
    /// <returns>
    /// <c>true</c> if the calling thread had an exclusive access that is now released by calling this method.
    /// <c>false</c> if the calling thread was not having an exclusive access.
    /// </returns>
    public bool ReleaseControl()
    {
        var tid = Environment.CurrentManagedThreadId;
        return Interlocked.CompareExchange(ref _data, 0, tid) == tid;
    }

    /// <summary>
    /// Try to gain exclusive access for the calling thread, trying for a given time span if needed.
    /// </summary>
    /// <param name="wait">
    /// The time span where the exclusive attempt should be repeatedly made.
    /// If <c>null</c> is passed, this method will wait indefinitely until it gain the exclusive access. Doing this could lead to endless waiting if your code
    /// is not dead stable.
    /// </param>
    /// <returns>
    /// <c>true</c> if the exclusive control was successfully made, <c>false</c> if it couldn't be made for the given time span.
    /// </returns>
    /// <remarks>
    /// If exclusive control was successfully taken, you must call <see cref="ReleaseControl"/> to release it when you're done. Failing to do so would cause
    /// other threads to possibly endlessly wait and/or not being able to mutate the resource controlled by this instance.
    /// </remarks>
    /// <example>
    /// A safe approach would be doing this
    /// <code>
    /// var hold = c.TakeControl(TimeSpan.FromMilliseconds(10));
    /// try
    /// {
    ///     if (hold)
    ///     {
    ///         //
    ///         // your code here
    ///         //
    ///     }
    /// }
    /// finally
    /// {
    ///     if (hold)
    ///     {
    ///         c.ReleaseControl();
    ///     }
    /// }
    /// </code>
    /// </example>
    public bool TakeControl(TimeSpan? wait=null)
    {
        var tid = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _data, tid, 0) == 0)
        {
            return true;
        }

        var bbb = new BurnBabyBurn(wait);
        while (bbb.Wait())
        {
            if (Interlocked.CompareExchange(ref _data, tid, 0) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Try to gain exclusive access for the calling thread
    /// </summary>
    /// <returns>
    /// <c>true</c> if the thread could take the exclusive control, <c>false</c> if the attempt failed because another thread currently holds the exclusive
    /// access.
    /// </returns>
    /// <remarks>
    /// This method won't wait, it will try to get the exclusive access and take it if possible or return a failure if it couldn't.
    /// If you are willing to wait to gain exclusive access, call <see cref="TakeControl"/> instead.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryTakeControl()
    {
        return Interlocked.CompareExchange(ref _data, Environment.CurrentManagedThreadId, 0) == 0;
    }

    #endregion

    #endregion

    #region Fields

    private int _data;

    #endregion

    #region Constructors

    /// <summary>
    /// Default constructor
    /// </summary>
    public ExclusiveAccessControl()
    {
        _data = 0;
    }

    #endregion
}