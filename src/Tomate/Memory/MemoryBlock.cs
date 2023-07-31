﻿using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Represent an allocated block of unmanaged memory
/// </summary>
/// <remarks>
/// Use one of the implementation of <see cref="IMemoryManager"/> to allocate a block.
/// Each instance of <see cref="MemoryBlock"/> has an internal RefCounter with an initial value of 1, that can be incremented with <see cref="AddRef"/>.
/// Releasing ownership is done by calling <see cref="Dispose"/>, when the RefCounter reaches 0, the memory block is released and no longer usable.
/// Each Memory Block has a Header that precedes its starting address (<see cref="BlockReferential.GenBlockHeader"/>), the RefCounter resides in this header
/// as well as the Allocator that owns the block.
/// Freeing a Memory Block instance will redirect the operation to the appropriate Allocator.
/// </remarks>
[PublicAPI]
public struct MemoryBlock : IDisposable
{
    /// <summary>
    /// The Memory Segment corresponding to the Memory Block
    /// </summary>
    /// <remarks>
    /// There is also an implicit casting operator <see cref="op_Implicit"/> that has the same function.
    /// </remarks>
    public MemorySegment MemorySegment;

    /// <summary>
    /// Construct a MemoryBlock from a MemorySegment
    /// </summary>
    /// <param name="memorySegment">The segment with a starting address that matches the block</param>
    /// <remarks>
    /// The size of the MemorySegment should match the real size of the MemoryBlock, higher would likely result to crash, lesser would be unpredictable.
    /// </remarks>
    public MemoryBlock(MemorySegment memorySegment)
    {
        MemorySegment = memorySegment;
    }

    internal unsafe MemoryBlock(byte* address, int length)
    {
        MemorySegment = new MemorySegment(address, length);
    }

    /// <summary>
    /// Extend the MemoryBlock lifetime by incrementing its Reference Counter.
    /// </summary>
    /// <returns>The new Reference Counter</returns>
    /// <remarks>
    /// A MemoryBlock can be shared among multiple threads, the only way to guarantee ownership is to call <see cref="AddRef"/> to extend it and a matching
    /// <see cref="Dispose"/> to release it.
    /// </remarks>
    public unsafe int AddRef()
    {
        ref var header = ref Unsafe.AsRef<BlockReferential.GenBlockHeader>(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
        return Interlocked.Increment(ref header.RefCounter);
    }

    /// <summary>
    /// Access the value of the Reference Counter
    /// </summary>
    /// <remarks>
    /// Increasing its value is done through <see cref="AddRef"/>, decreasing by calling <see cref="Dispose"/>. The MemoryBlock is freed when the counter
    /// reaches 0.
    /// </remarks>
    public unsafe int RefCounter
    {
        get
        {
            ref var header = ref Unsafe.AsRef<BlockReferential.GenBlockHeader>(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
            return header.RefCounter;
        }
    }

    /// <summary>
    /// If <c>true</c> the instance doesn't refer to a valid MemoryBlock
    /// </summary>
    public bool IsDefault => MemorySegment.IsDefault;
    /// <summary>
    /// If <c>true</c> the instance is not valid and considered as <c>Default</c> (<see cref="IsDefault"/> will be also <c>true</c>).
    /// </summary>
    public bool IsDisposed => MemorySegment.IsDefault;

    /// <summary>
    /// Attempt to Dispose and free the MemoryBlock
    /// </summary>
    /// <remarks>
    /// If the instance is valid (<see cref="IsDefault"/> is <c>false</c>), it simply defer to <see cref="BlockReferential.Free"/>.
    /// If the <see cref="RefCounter"/> is equal to <c>1</c>, then the MemoryBlock will be freed.
    /// </remarks>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (BlockReferential.Free(this))
        {
            MemorySegment = default;
        }
    }

    public MemoryBlock<T> Cast<T>() where T : unmanaged => new(MemorySegment.Cast<T>());
    public static implicit operator MemorySegment(MemoryBlock mb) => mb.MemorySegment;
    public static implicit operator MemoryBlock(MemorySegment seg) => new(seg);
}

[PublicAPI]
public struct MemoryBlock<T> : IDisposable where T : unmanaged
{
    public MemorySegment<T> MemorySegment;

    public MemoryBlock(MemorySegment<T> memorySegment)
    {
        MemorySegment = memorySegment;
    }

    public unsafe int AddRef()
    {
        ref var header = ref Unsafe.AsRef<BlockReferential.GenBlockHeader>(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
        return Interlocked.Increment(ref header.RefCounter);
    }

    public unsafe int RefCounter
    {
        get
        {
            ref var header = ref Unsafe.AsRef<BlockReferential.GenBlockHeader>(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
            return header.RefCounter;
        }
    }

    public bool IsDefault => MemorySegment.IsDefault;
    public bool IsDisposed => MemorySegment.IsDefault;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (BlockReferential.Free(this))
        {
            MemorySegment = default;
        }
    }

    public static implicit operator MemorySegment<T>(MemoryBlock<T> mb) => mb.MemorySegment;
    public static implicit operator MemoryBlock(MemoryBlock<T> mb) => new(mb.MemorySegment.Cast());
    public static implicit operator MemoryBlock<T>(MemorySegment<T> seg) => new(seg);
}