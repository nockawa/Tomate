using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Static class that references all the <see cref="IBlockAllocator"/> to ensure a generic free of any allocated <see cref="MemoryBlock"/>.
/// </summary>
[PublicAPI]
public static class BlockReferential
{
    /// <summary>
    /// Each <see cref="MemoryBlock"/> allocated through a <see cref="IMemoryManager"/> based allocator must contain this header BEFORE the block's starting
    /// address.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryBlock"/> instances must be free-able without specifying the allocator that owns it and also supporting thread-safe lifetime related
    /// operations, so this header is the way to ensure all of this.
    /// More specifically, <see cref="IBlockAllocator"/> is the interface that takes care of freeing a <see cref="MemoryBlock"/> and the way to indentify
    /// which particular instance of the Block Allocator is through the <see cref="GenBlockHeader.BlockId"/> property.
    /// Be sure to check out <see cref="DefaultMemoryManager"/> to get and understand how things are working. 
    /// </remarks>
    [PublicAPI]
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct GenBlockHeader
    {
        private const int BlockIdMask = 0xFFFFFF;
        private const int FreeFlag = 0x1000000;                 // If set, the segment is free
        
        /// <summary>
        /// The actual RefCounter of the <see cref="MemoryBlock"/>
        /// </summary>
        /// <remarks>
        /// You should update this counter only through <see cref="Interlocked.Increment(ref int)"/> or <see cref="Interlocked.Decrement(ref int)"/> to
        /// ensure thread-safeness
        /// </remarks>
        public int RefCounter;

        // A mix of BlockId and IsFree
        private int _data;
        
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

    /// <summary>
    /// Register a given Block Allocator instance and get its BlockId
    /// </summary>
    /// <param name="allocator">The instance to register</param>
    /// <returns>The Id that must be stored in <see cref="GenBlockHeader.BlockId"/> property</returns>
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