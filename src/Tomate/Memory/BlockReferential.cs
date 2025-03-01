﻿using System.Diagnostics;
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
    #region Constants

    private static readonly List<IBlockAllocator> Allocators;
    private static readonly Stack<int> AvailableSlots;

    public const int MaxReferencedBlockCount = 0x1000000;

    #endregion

    #region Public APIs

    #region Methods

    public static unsafe bool Free(MemoryBlock block)
    {
        // Special case, zero size block are not allocated, so not freed either
        if (block.MemorySegment.Length == 0)
        {
            return true;
        }
        
        Debug.Assert(block.MemorySegment.Address != null, "Can't free empty MemoryBlock");
#if DEBUGALLOC
        ref var header = ref Unsafe.AsRef<GenBlockHeader>(block.MemorySegment.Address - DefaultMemoryManager.BlockMarginSize - sizeof(GenBlockHeader));
#else
        ref var header = ref Unsafe.AsRef<GenBlockHeader>(block.MemorySegment.Address - sizeof(GenBlockHeader));
#endif

        // Most significant bit to one mean the MemoryBlock is from a MMF, the encoded value is different as MMF are interprocess, thus address independent
        if (header.IsFromMMF)
        {
            var mmf = block.MemoryManager as MemoryManagerOverMMF;
            // We need to identify which MMF this MemoryBlock is from, for that we use the address range the MMF is using
            Debug.Assert(mmf != null, "Trying to free a MemoryBlock from a MemoryMappedFile that has already been disposed");
            // ReSharper disable once ConstantConditionalAccessQualifier
            // ReSharper disable once ConstantNullCoalescingCondition
            return mmf?.Free(block) ?? false;
        }
        else
        {
            var blockId = header.BlockIndex;
            var allocator = Allocators[blockId];
            if (allocator == null)
            {
                throw new InvalidOperationException("No allocated are currently registered with this Id");
            }

            return allocator.Free(block);
        }
    }

    public static unsafe IMemoryManager GetMemoryManager(MemoryBlock block)
    {
        if (block.MemorySegment.IsDefault)
        {
            return null;
        }
#if DEBUGALLOC
        ref var header = ref Unsafe.AsRef<GenBlockHeader>(block.MemorySegment.Address - DefaultMemoryManager.BlockMarginSize - sizeof(GenBlockHeader));
#else
        ref var header = ref Unsafe.AsRef<GenBlockHeader>(block.MemorySegment.Address - sizeof(GenBlockHeader));
#endif

        // Most significant bit to one mean the MemoryBlock is from a MMF, the encoded value is different as MMF are interprocess, thus address independent
        if (header.IsFromMMF)
        {
            return MMFRegistry.MMFById[block.MemorySegment.MMFId];
        }
        else
        {
            var blockId = header.BlockIndex;
            var allocator = Allocators[blockId];
            if (allocator == null)
            {
                throw new InvalidOperationException("No allocated are currently registered with this Id");
            }

            return allocator.Owner;
        }
    }

    public static bool Resize(ref MemoryBlock block, int newLength, bool zeroExtra=false)
    {
        if (block.IsDefault || block.IsDisposed)
        {
            // TODO Exception?
            return false;
        }
        var mm = block.MemoryManager;
        mm.Resize(ref block, newLength);
        return true;
    }

    public static bool Resize<T>(ref MemoryBlock<T> block, int newLength, bool zeroExtra=false) where T : unmanaged
    {
        if (block.IsDefault || block.IsDisposed)
        {
            // TODO Exception?
            return false;
        }
        var mm = block.MemoryManager;
        mm.Resize(ref block, newLength);
        return true;
    }

    public static void UnregisterAllocator(int index)
    {
        try
        {
            _control.TakeControl();
            var allocator = Allocators[index];
            allocator.Dispose();
            AvailableSlots.Push(index);
        }
        finally
        {
            _control.ReleaseControl();
        }
    }

    #endregion

    #endregion

    #region Fields

    private static ExclusiveAccessControl _control;

    #endregion

    #region Constructors

    static BlockReferential()
    {
        Allocators = new List<IBlockAllocator>(1024);
        AvailableSlots = new Stack<int>(128);
    }

    #endregion

    #region Internals

    #region Internals methods

    /// <summary>
    /// Register a given Block Allocator instance and get its index (or BlockIndex)
    /// </summary>
    /// <param name="allocator">The instance to register</param>
    /// <returns>The Id that must be stored in <see cref="GenBlockHeader.BlockIndex"/> property</returns>
    internal static int RegisterAllocator(IBlockAllocator allocator)
    {
        try
        {
            _control.TakeControl();

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

    #endregion

    #endregion

    #region Inner types

    /// <summary>
    /// Each <see cref="MemoryBlock"/> allocated through a <see cref="IMemoryManager"/> based allocator must contain this header BEFORE the block's starting
    /// address.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryBlock"/> instances must be free-able without specifying the allocator that owns it and also supporting thread-safe lifetime related
    /// operations, so this header is the way to ensure all of this.
    /// More specifically, <see cref="IBlockAllocator"/> is the interface that takes care of freeing a <see cref="MemoryBlock"/> and the way to identify
    /// which particular instance of the Memory Manager is through the <see cref="BlockIndex"/> property.
    /// Be sure to check out <see cref="DefaultMemoryManager"/> to get and understand how things are working. 
    /// </remarks>
    [PublicAPI]
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct GenBlockHeader
    {
        #region Constants

        private const uint BlockIndexMask   = 0x3FFFFFFF;
        private const uint FreeFlag         = 0x80000000;                 // If set, the segment is free
        private const uint MMFFlag          = 0x40000000;                 // If set, the segment is from an MMF

        #endregion

        #region Public APIs

        #region Properties

        public int BlockIndex
        {
            get => (int)(_data & BlockIndexMask);
            set => _data = ((uint)value | (_data & ~BlockIndexMask));
        }

        public bool IsFree
        {
            get => (_data & FreeFlag) != 0;
            set => _data = (_data & ~FreeFlag) | (value ? FreeFlag : 0);
        }

        public bool IsFromMMF
        {
            get => (_data & MMFFlag) != 0;
            set => _data = (_data & ~MMFFlag) | (value ? MMFFlag : 0);
        }

        #endregion

        #endregion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _data = 0;
            RefCounter = 0;
        }
        #region Fields

        /// <summary>
        /// The actual RefCounter of the <see cref="MemoryBlock"/>
        /// </summary>
        /// <remarks>
        /// You should update this counter only through <see cref="Interlocked.Increment(ref int)"/> or <see cref="Interlocked.Decrement(ref int)"/> to
        /// ensure thread-safeness
        /// </remarks>
        public int RefCounter;

        // A mix of BlockId and IsFree
        private uint _data;

        #endregion
    }

    #endregion
}