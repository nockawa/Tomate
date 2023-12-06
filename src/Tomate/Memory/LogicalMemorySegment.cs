using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Stores the location and size of a logical memory segment
/// </summary>
/// <remarks>
/// Define a memory segment inside another one using an offset and a size.
/// This structure is the most compact one, <see cref="ToSpan{T}"/> performance is a little bit slower than <see cref="MemorySegment"/>'s implementation.
/// </remarks>
[PublicAPI]
public readonly unsafe struct LogicalMemorySegment
{
    #region Public APIs

    #region Methods

    public Span<T> ToSpan<T>(void* baseAddr) where T : unmanaged
    {
        return new Span<T>((byte*)baseAddr + Offset, Size / sizeof(T));
    }

    #endregion

    #endregion

    #region Fields

    public readonly int Offset;
    public readonly int Size;

    #endregion

    #region Constructors

    public LogicalMemorySegment(int offset, int size)
    {
        Offset = offset;
        Size = size;
    }

    #endregion
}
