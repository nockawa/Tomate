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
}