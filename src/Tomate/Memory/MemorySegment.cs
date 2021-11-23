using System;

namespace Tomate;

/// <summary>
/// Define a memory segment stored at a fixed address
/// </summary>
/// <remarks>
/// This is certainly the fastest way to store/use a memory segment. This allows us to store instances of this struct everywhere we want (as opposed
/// to <see cref="Span{T}"/> which can't be stored in type declarations).
/// The cast/conversion to <see cref="Span{T}"/> is as fast as it could be and the user should rely on <see cref="Span{T}"/> as much as possible, even
/// through direct memory access will be slightly faster.
/// Only work with this type if you are dealing with pinned memory block, otherwise rely on <see cref="Span{T}"/>
/// </remarks>
public readonly unsafe struct MemorySegment
{
    public readonly byte* Address;
    public readonly int Size;

    public MemorySegment(byte* address, int size)
    {
        Address = address;
        Size = size;
    }

    public Span<T> ToSpan<T>() where T : unmanaged => new(Address, Size / sizeof(T));
    public static implicit operator Span<byte>(MemorySegment segment)
    {
        return new Span<byte>(segment.Address, segment.Size);
    }
}