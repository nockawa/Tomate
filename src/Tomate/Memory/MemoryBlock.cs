using System.Runtime.CompilerServices;

namespace Tomate;

public struct MemoryBlock : IDisposable
{
    public MemorySegment MemorySegment;

    public MemoryBlock(MemorySegment memorySegment)
    {
        MemorySegment = memorySegment;
    }

    internal unsafe MemoryBlock(byte* address, int length)
    {
        MemorySegment = new MemorySegment(address, length);
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

    public bool IsDisposed => MemorySegment.IsEmpty;

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
}

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

    public bool IsDisposed => MemorySegment.IsEmpty;

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
}