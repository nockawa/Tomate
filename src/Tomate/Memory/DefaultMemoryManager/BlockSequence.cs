namespace Tomate;

public partial class DefaultMemoryManager
{
    internal class BlockSequence
    {
        internal struct DebugData
        {
            public long TotalCommitted;
            public long TotalAllocatedMemory;
            public long TotalFreeMemory;
            public long ScanFreeListCount;
            public int FreeSegmentDefragCount;
            public int AllocatedSegmentCount;
            public int FreeSegmentCount;
            public int TotalHeaderSize;
            public int TotalPaddingSize;
            public int TotalBlockCount;

            public bool IsCoherent => TotalCommitted == TotalAllocatedMemory + TotalFreeMemory + TotalHeaderSize + TotalPaddingSize;
        }

        private ExclusiveAccessControl _control;
        private SmallBlock _firstSmallBlock;
        private LargeBlock _firstLargeBlock;

        public DefaultMemoryManager Owner { get; }
        internal DebugData DebugInfo;

        public BlockSequence(DefaultMemoryManager owner)
        {
            Owner = owner;
            _control = new ExclusiveAccessControl();
            _firstSmallBlock = owner.AllocateSmallBlock(this);
            DebugInfo.TotalBlockCount = 1;
        }

        public MemorySegment Allocate(int size)
        {
            if (size > MemorySegmentMaxSizeForSmallBlock)
            {
                var curBlock = _firstLargeBlock;
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
                                var newBlock = Owner.AllocateLargeBlock(this, size);
                                var next = _firstLargeBlock;
                                _firstLargeBlock = newBlock;
                                newBlock.NextBlock = next;

                                curBlock = newBlock;

                                ++DebugInfo.TotalBlockCount;
                            }
                        }
                        finally
                        {
                            _control.ReleaseControl();
                        }
                    }

                    if (curBlock.DoAllocate(size, ref DebugInfo, out var seg))
                    {
                        return seg;
                    }

                    curBlock = curBlock.NextBlock;
                }
            }
            else
            {
                var curBlock = _firstSmallBlock;
                while (true)
                {
                    if (curBlock.DoAllocate(size, ref DebugInfo, out var seg))
                    {
                        return seg;
                    }

                    // The first block couldn't make the allocation, go further in the block sequence. If there's no block after, we need to create one and
                    //  this must be made under a lock. Let's use the double-check lock pattern to avoid locking every-time there's already a block next.

                    // No block, create one...but under a lock
                    if (curBlock.NextBlock == null)
                    {
                        try
                        {
                            // Lock
                            _control.TakeControl(null);

                            // Another thread may have beaten us, so check if it's the case or not
                            if (curBlock.NextBlock == null)
                            {
                                var newBlock = Owner.AllocateSmallBlock(this);
                                var next = _firstSmallBlock;
                                _firstSmallBlock = newBlock;
                                newBlock.NextBlock = next;

                                curBlock = newBlock;

                                ++DebugInfo.TotalBlockCount;
                            }
                            else
                            {
                                // If a concurrent thread already made the allocation, take its block as the new one
                                curBlock = curBlock.NextBlock;
                            }
                        }
                        finally
                        {
                            _control.ReleaseControl();
                        }
                    }
                    else
                    {
                        curBlock = curBlock.NextBlock;
                    }
                }
            }
        }

        internal void RecycleBlock(SmallBlock block)
        {
            try
            {
                _control.TakeControl(null);

                // If we're removing the first block of the linked list
                if (_firstSmallBlock == block)
                {
                    // Only release the first block if there's one after, otherwise there wouldn't be any block and there's no point to that
                    if (_firstSmallBlock.NextBlock == null)
                    {
                        return;
                    }
                    _firstSmallBlock = _firstSmallBlock.NextBlock;
                }
                else
                {
                    // Find the block in the linked list and remove it
                    var curBlock = _firstSmallBlock;
                    while (curBlock != null)
                    {
                        if (curBlock.NextBlock == block)
                        {
                            curBlock.NextBlock = curBlock.NextBlock.NextBlock;
                            break;
                        }

                        curBlock = curBlock.NextBlock;
                    }
                }

                --DebugInfo.TotalBlockCount;

                block.Recycle();
                Owner.RecycleBlock(block);
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        internal void RecycleBlock(LargeBlock block)
        {
            try
            {
                _control.TakeControl(null);

                // If we're removing the first block of the linked list
                if (_firstLargeBlock == block)
                {
                    // Only release the first block if there's one after, otherwise there wouldn't be any block and there's no point to that
                    if (_firstLargeBlock.NextBlock != null)
                    {
                        _firstLargeBlock = _firstLargeBlock.NextBlock;
                    }
                }
                else
                {
                    // Find the block in the linked list and remove it
                    var curBlock = _firstLargeBlock;
                    while (curBlock != null)
                    {
                        if (curBlock.NextBlock == block)
                        {
                            curBlock.NextBlock = curBlock.NextBlock.NextBlock;
                            break;
                        }

                        curBlock = curBlock.NextBlock;
                    }
                }

                --DebugInfo.TotalBlockCount;
            }
            finally
            {
                _control.ReleaseControl();
            }

            block.Recycle();
            Owner.RecycleBlock(block);
        }

        internal void DefragmentFreeSegments()
        {
            try
            {
                _control.TakeControl(null);

                var curBlock = _firstSmallBlock;
                while (curBlock != null)
                {
                    curBlock.DefragmentFreeSegments(ref DebugInfo);

                    curBlock = curBlock.NextBlock;
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }
    }
}