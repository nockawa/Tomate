using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Tomate;

public class SegmentConstructException : Exception
{
    public SegmentConstructException(string message) : base(message)
    {
        
    }
}

public class ItemSetTooBigException : Exception
{
    public ItemSetTooBigException(string message) : base(message)
    {

    }
}

public class CapacityTooBigException : Exception
{
    public CapacityTooBigException(string message) : base(message)
    {

    }
}

[StackTraceHidden]
internal static class ThrowHelper
{
    [DoesNotReturn]
    internal static void ObjectDisposed(string objectName, string message)
    {
        throw new ObjectDisposedException(objectName, message);
    }

    [DoesNotReturn]
    internal static void OutOfMemory(string message)
    {
        throw new OutOfMemoryException(message);
    }

    [DoesNotReturn]
    internal static void OutOfRange(string message)
    {
        throw new IndexOutOfRangeException(message);
    }

    [DoesNotReturn]
    internal static void StringTooBigForString64(string paramName, string source)
    {
        throw new ArgumentOutOfRangeException(paramName, $"The given string '{source}' is bigger than the maximum allowed size (63 bytes).");
    }

    [DoesNotReturn]
    internal static void BlockSimpleDicDefKeyNotAllowed()
    {
        throw new ArgumentException("The key must not be of 'default(TKey)'", "key");
    }

    [DoesNotReturn]
    internal static void NeedNonNegIndex(string paramName)
    {
        throw new ArgumentOutOfRangeException(paramName, "Index can't be a negative value");
    }

    [DoesNotReturn]
    internal static void EmptyStack()
    {
        throw new InvalidOperationException("Cannot perform operation, stack is empty");
    }

    [DoesNotReturn]
    internal static void EmptyQueue()
    {
        throw new InvalidOperationException("Cannot perform operation, queue is empty");
    }

    [DoesNotReturn]
    internal static void TimeSegmentConstructError(long start, long end)
    {
        throw new SegmentConstructException($"Cannot construct TimeSegment instance because start ({start}) is greater than end ({end})");
    }

    [DoesNotReturn]
    internal static void AppendCollectionItemSetTooBig(int requestedSize, int maxAllowed)
    {
        throw new ItemSetTooBigException($"Can't allocate the requested item number ({requestedSize}), the maximum allowed is {maxAllowed}");
    }

    [DoesNotReturn]
    internal static void AppendCollectionCapacityTooBig(int requestedCapacity, int maxAllowed)
    {
        throw new CapacityTooBigException($"Can't create a MappedAppendCollection withe the given capacity ({requestedCapacity}), the maximum allowed is {maxAllowed}");
    }

    [DoesNotReturn]
    internal static void SmallLockConcurrencyCapacityReached(int maxConcurrency)
    {
        throw new SmallLockConcurrencyExceededException($"Can't enter lock, the maximum concurrency level ({maxConcurrency}) has been reached.");
    }

    [DoesNotReturn]
    internal static void SmallLockConcurrencyTooBig(int maxConcurrency)
    {
        throw new SmallLockConcurrencyExceededException($"Maximum Concurrency exceeded, max must be at most {ushort.MaxValue}, but {maxConcurrency} was specified at construction");
    }

    [DoesNotReturn]
    internal static void SmallLockTooManyExit()
    {
        throw new Exception("Exit has been called too many times");
    }

    [DoesNotReturn]
    internal static void SmallLockBadLockId(ulong expected, ulong actual)
    {
        throw new ArgumentException($"Trying to unlock with [{expected.HighS()};{expected.LowS()}] but [{actual.HighS()};{actual.LowS()}] is actually on the top of the queue", "lockId");
    }

    [DoesNotReturn]
    internal static void KeyNotFound<T>(T key)
    {
        throw new KeyNotFoundException($"The given key {key} was not found.");
    }
}