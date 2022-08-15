using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

public static class BlockReferential
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct GenBlockHeader
    {
        private const int BlockIdMask = 0xFFFFFF;
        private const int FreeFlag = 0x1000000;                 // If set, the segment is free
        
        public int RefCounter;
        public int _data;
        
        public int BlockId
        {
            get => _data & BlockIdMask;
            set => _data = (value | (_data & ~BlockIdMask));
        }

        public bool IsFree
        {
            get => (_data & FreeFlag) != 0;
            set => _data = (_data & ~FreeFlag) | (value ? FreeFlag : 0);
        }
    }

    public const int MaxReferencedBlockCount = 0x1000000;
    private static ExclusiveAccessControl _control;
    private static readonly List<IBlockAllocator> Allocators;
    private static readonly Stack<int> AvailableSlots;

    static BlockReferential()
    {
        Allocators = new List<IBlockAllocator>(1024);
        AvailableSlots = new Stack<int>(128);
    }

    public static int RegisterAllocator(IBlockAllocator allocator)
    {
        try
        {
            _control.TakeControl(null);

            int res;
            if (AvailableSlots.Count > 0)
            {
                res = AvailableSlots.Pop();
                Allocators[res] = allocator;
            }
            else
            {
                res = Allocators.Count;
                Debug.Assert(res < MaxReferencedBlockCount, $"Too many block are being referenced, {MaxReferencedBlockCount} is the maximum allowed");
                Allocators.Add(allocator);
            }

            return res;
        }
        finally
        {
            _control.ReleaseControl();
        }
    }

    public static void ReleaseAllocator(int index)
    {
        try
        {
            _control.TakeControl(null);
            var allocator = Allocators[index];
            allocator.Dispose();
            AvailableSlots.Push(index);
        }
        finally
        {
            _control.ReleaseControl();
        }
    }

    public static unsafe bool Free(MemoryBlock block)
    {
        Debug.Assert(block.MemorySegment.Address != null, "Can't free empty MemoryBlock");
        ref var header = ref Unsafe.AsRef<GenBlockHeader>(block.MemorySegment.Address - sizeof(GenBlockHeader));
        var blockId = header.BlockId;
        var allocator = Allocators[blockId];
        if (allocator == null)
        {
            throw new InvalidOperationException("No allocated are currently registered with this Id");
        }

        return allocator.Free(block);
    }
}