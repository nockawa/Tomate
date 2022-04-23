using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Tomate;

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
}