// ReSharper disable once RedundantUsingDirective
using System.Text;

namespace Tomate;

public partial class DefaultMemoryManager
{
    #region Inner types

    internal class BlockAllocatorSequence : IDisposable
    {
        internal struct DebugData
        {
            #region Public APIs

            #region Properties

            public bool IsCoherent => TotalCommitted == TotalAllocatedMemory + TotalFreeMemory + TotalHeaderSize + TotalPaddingSize;

            #endregion

            #endregion

            #region Fields

            public int AllocatedSegmentCount;
            public int FreeSegmentCount;
            public int FreeSegmentDefragCount;
            public long ScanFreeListCount;
            public long TotalAllocatedMemory;
            public int TotalBlockCount;
            public long TotalCommitted;
            public long TotalFreeMemory;
            public int TotalHeaderSize;
            public int TotalPaddingSize;

            #endregion
        }

        private ExclusiveAccessControl _control;
        private SmallBlockAllocator _firstSmallBlockAllocator;
        private LargeBlockAllocator _firstLargeBlockAllocator;

        public DefaultMemoryManager Owner { get; private set; }
        internal DebugData DebugInfo;
        internal readonly int FirstBlockId;

        public BlockAllocatorSequence(DefaultMemoryManager owner)
        {
            Owner = owner;
            _control = new ExclusiveAccessControl();
            _firstSmallBlockAllocator = owner.AllocateSmallBlockAllocator(this);
            DebugInfo.TotalBlockCount = 1;
            FirstBlockId = _firstSmallBlockAllocator.BlockIndex;
        }

        public bool IsDisposed => Owner == null;

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            {
                var block = _firstSmallBlockAllocator;
                while (block != null)
                {
                    block.Dispose();
                    block = block.NextBlockAllocator;
                }
            }
            {
                var block = _firstLargeBlockAllocator;
                while (block != null)
                {
                    block.Dispose();
                    block = block.NextBlockAllocator;
                }
            }
        }

        public MemoryBlock Allocate(ref MemoryBlockInfo info)
        {
            var size = info.Size;
            if (size > MemorySegmentMaxSizeForSmallBlock)
            {
                var curBlock = _firstLargeBlockAllocator;
                while (true)
                {
                    // No block, create one...but under a lock
                    if (curBlock == null)
                    {
                        try
                        {
                            // Lock
                            _control.TakeControl(null);

                            // Another thread may have beaten us, so check if it's the case or not
                            if (curBlock == null)
                            {
                                var newBlock = Owner.AllocateLargeBlockAllocator(this, size);
                                var next = _firstLargeBlockAllocator;
                                _firstLargeBlockAllocator = newBlock;
                                newBlock.NextBlockAllocator = next;

                                curBlock = newBlock;

                                ++DebugInfo.TotalBlockCount;
                            }
                        }
                        finally
                        {
                            _control.ReleaseControl();
                        }
                    }

                    if (curBlock.DoAllocate(ref info, ref DebugInfo, out var seg))
                    {
                        return seg;
                    }

                    curBlock = curBlock.NextBlockAllocator;
                }
            }
            else
            {
                var curBlock = _firstSmallBlockAllocator;
                while (true)
                {
                    if (curBlock.DoAllocate(ref info, ref DebugInfo, out var seg))
                    {
                        return seg;
                    }

                    // The first block couldn't make the allocation, go further in the block sequence. If there's no block after, we need to create one and
                    //  this must be made under a lock. Let's use the double-check lock pattern to avoid locking every-time there's already a block next.

                    // No block, create one...but under a lock
                    if (curBlock.NextBlockAllocator == null)
                    {
                        try
                        {
                            // Lock
                            _control.TakeControl(null);

                            // Another thread may have beaten us, so check if it's the case or not
                            if (curBlock.NextBlockAllocator == null)
                            {
                                var newBlock = Owner.AllocateSmallBlockAllocator(this);
                                var next = _firstSmallBlockAllocator;
                                _firstSmallBlockAllocator = newBlock;
                                newBlock.NextBlockAllocator = next;

                                curBlock = newBlock;

                                ++DebugInfo.TotalBlockCount;
                            }
                            else
                            {
                                // If a concurrent thread already made the allocation, take its block as the new one
                                curBlock = curBlock.NextBlockAllocator;
                            }
                        }
                        finally
                        {
                            _control.ReleaseControl();
                        }
                    }
                    else
                    {
                        curBlock = curBlock.NextBlockAllocator;
                    }
                }
            }
        }

        internal void RecycleBlock(SmallBlockAllocator blockAllocator)
        {
            try
            {
                _control.TakeControl(null);

                // If we're removing the first block of the linked list
                if (_firstSmallBlockAllocator == blockAllocator)
                {
                    // Only release the first block if there's one after, otherwise there wouldn't be any block and there's no point to that
                    if (_firstSmallBlockAllocator.NextBlockAllocator == null)
                    {
                        return;
                    }
                    _firstSmallBlockAllocator = _firstSmallBlockAllocator.NextBlockAllocator;
                }
                else
                {
                    // Find the block in the linked list and remove it
                    var curBlock = _firstSmallBlockAllocator;
                    while (curBlock != null)
                    {
                        if (curBlock.NextBlockAllocator == blockAllocator)
                        {
                            curBlock.NextBlockAllocator = curBlock.NextBlockAllocator.NextBlockAllocator;
                            break;
                        }

                        curBlock = curBlock.NextBlockAllocator;
                    }
                }

                --DebugInfo.TotalBlockCount;

                blockAllocator.Recycle();
                Owner.RecycleBlock(blockAllocator);
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        internal void RecycleBlock(LargeBlockAllocator blockAllocator)
        {
            try
            {
                _control.TakeControl(null);

                // If we're removing the first block of the linked list
                if (_firstLargeBlockAllocator == blockAllocator)
                {
                    // Only release the first block if there's one after, otherwise there wouldn't be any block and there's no point to that
                    if (_firstLargeBlockAllocator.NextBlockAllocator == null)
                    {
                        return;
                    }
                    _firstLargeBlockAllocator = _firstLargeBlockAllocator.NextBlockAllocator;
                }
                else
                {
                    // Find the block in the linked list and remove it
                    var curBlock = _firstLargeBlockAllocator;
                    while (curBlock != null)
                    {
                        if (curBlock.NextBlockAllocator == blockAllocator)
                        {
                            curBlock.NextBlockAllocator = curBlock.NextBlockAllocator.NextBlockAllocator;
                            break;
                        }

                        curBlock = curBlock.NextBlockAllocator;
                    }
                }

                --DebugInfo.TotalBlockCount;
            }
            finally
            {
                _control.ReleaseControl();
            }

            blockAllocator.Recycle();
            Owner.RecycleBlock(blockAllocator);
        }

        internal void DefragmentFreeSegments()
        {
            try
            {
                _control.TakeControl(null);

                var curBlock = _firstSmallBlockAllocator;
                while (curBlock != null)
                {
                    curBlock.DefragmentFreeSegments(ref DebugInfo);

                    curBlock = curBlock.NextBlockAllocator;
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

#if DEBUGALLOC
        public void DumpLeaks(StringBuilder sb, ref int totalLeakCount)
        {
            {
                var block = _firstSmallBlockAllocator;
                while (block != null)
                {
                    block.DumpLeaks(sb, ref totalLeakCount);
                    block = block.NextBlockAllocator;
                }
            }

            {
                var block = _firstLargeBlockAllocator;
                while (block != null)
                {
                    block.DumpLeaks(sb, ref totalLeakCount);
                    block = block.NextBlockAllocator;
                }
            }
        }
#endif
    }

    #endregion
}