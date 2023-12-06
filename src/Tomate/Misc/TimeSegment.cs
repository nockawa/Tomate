using System.Diagnostics;
using JetBrains.Annotations;

namespace Tomate;

[DebuggerDisplay("[{Begin},{End}")]
[PublicAPI]
public readonly struct TimeSegment
{
    #region Public APIs

    #region Properties

    public TimeSpan Begin => new(BeginTicks);

    public long BeginTicks { get; }
    public long DeltaTick => EndTicks - BeginTicks;

    public TimeSpan Duration => new(EndTicks - BeginTicks);
    public TimeSpan End => new(EndTicks);
    public long EndTicks { get; }

    #endregion

    #region Methods

    public static TimeSegment operator +(long offset, TimeSegment ts) => new(offset + ts.BeginTicks, offset + ts.EndTicks);

    public bool IsOverlapping(TimeSegment other) => other.EndTicks > BeginTicks || other.BeginTicks < EndTicks;

    public override string ToString()
    {
        return $"Begin: {Begin}, End: {End}";
    }

    #endregion

    #endregion

    #region Constructors

    public TimeSegment(long beginTicks, long endTicks)
    {
        if (beginTicks > endTicks)
        {
            ThrowHelper.TimeSegmentConstructError(beginTicks, endTicks);
        }

        BeginTicks = beginTicks;
        EndTicks = endTicks;
    }

    public TimeSegment(TimeSpan begin, TimeSpan end) : this(begin.Ticks, end.Ticks)
    {
    }

    #endregion
}