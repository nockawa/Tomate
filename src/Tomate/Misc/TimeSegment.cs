using System.Diagnostics;

namespace Tomate;

[DebuggerDisplay("[{Begin},{End}")]
public readonly struct TimeSegment
{
    public override string ToString()
    {
        return $"Begin: {Begin}, End: {End}";
    }

    public long BeginTicks { get; }
    public long EndTicks { get; }

    public TimeSpan Begin => new(BeginTicks);
    public TimeSpan End => new(EndTicks);

    public TimeSpan Duration => new(EndTicks - BeginTicks);
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

    public bool IsOverlapping(TimeSegment other) => other.EndTicks > BeginTicks || other.BeginTicks < EndTicks;

    public static TimeSegment operator +(long offset, TimeSegment ts) => new(offset + ts.BeginTicks, offset + ts.EndTicks);
}