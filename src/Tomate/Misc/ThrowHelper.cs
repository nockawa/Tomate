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
        throw new CapacityTooBigException($"Can't create an AppendCollection withe the given capacity ({requestedCapacity}), the maximum allowed is {maxAllowed}");
    }
}