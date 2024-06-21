using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace Tomate.Tests;

public class DefaultMemoryManagerTests
{
    private readonly struct Masks
    {
        public Masks(byte v)
        {
            _256 = Vector256.Create(v);
            _128 = Vector128.Create(v);
            _64 = ((ulong)v << 0) | ((ulong)v << 8) | ((ulong)v << 16) | ((ulong)v << 24) | ((ulong)v << 32) | ((ulong)v << 40) | ((ulong)v << 48) | ((ulong)v << 56);
            _32 = ((uint)v << 0) | ((uint)v << 8) | ((uint)v << 16) | ((uint)v << 24);
            _16 = (ushort)(((ushort)v << 0) | ((ushort)v << 8));
            _8 = v;
        }

        private readonly Vector256<byte> _256;
        private readonly Vector128<byte> _128;
        private readonly ulong _64;
        private readonly uint _32;
        private readonly ushort _16;
        private readonly byte _8;

        public unsafe void Fill(MemorySegment<byte> segment)
        {
            var cur = segment.Address;
            var end = cur + segment.Length;
            while (cur < end)
            {
                var remaining = (int)(end - cur);
                if (remaining >= 32)
                {
                    _256.Store(cur);
                    cur += 32;
                }
                else if (remaining >= 16)
                {
                    _128.Store(cur);
                    cur += 16;
                }
                else if (remaining >= 8)
                {
                    *((ulong*)cur) = _64;
                    cur += 8;
                }
                else if (remaining >= 4)
                {
                    *((uint*)cur) = _32;
                    cur += 4;
                }
                else if (remaining >= 2)
                {
                    *((ushort*)cur) = _16;
                    cur += 2;
                }
                else
                {
                    *cur = _8;
                    cur ++;
                }
            }
        }

        public unsafe bool Check(MemorySegment<byte> segment)
        {
            var cur = segment.Address;
            var end = cur + segment.Length;
            while (cur < end)
            {
                var remaining = (int)(end - cur);
                if (remaining >= 32)
                {
                    var v = Vector256.Load(cur);
                    if (v != _256)
                    {
                        return false;
                    }
                    cur += 32;
                }
                else if (remaining >= 16)
                {
                    var v = Vector128.Load(cur);
                    if (v != _128)
                    {
                        return false;
                    }
                    cur += 16;
                }
                else if (remaining >= 8)
                {
                    if (*((ulong*)cur) != _64)
                    {
                        return false;
                    }
                    cur += 8;
                }
                else if (remaining >= 4)
                {
                    if (*((uint*)cur) != _32)
                    {
                        return false;
                    }
                    cur += 4;
                }
                else if (remaining >= 2)
                {
                    if (*((ushort*)cur) != _16)
                    {
                        return false;
                    }
                    cur += 2;
                }
                else
                {
                    if (*cur != _8)
                    {
                        return false;
                    }
                    cur++;
                }
            }

            return true;
        }
    }

    private static Masks[] _masks;

    static DefaultMemoryManagerTests()
    {
        _masks = new Masks[256];
        for (int i = 0; i < 256; i++)
        {
            _masks[i] = new Masks((byte)i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void FillSegment(MemorySegment segment, byte startVal)
    {
        var data = segment.Cast<byte>();
        _masks[startVal].Fill(data);
    }

    [Test]
    [TestCase(0.5f)]
    [TestCase(1.0f)]
    [TestCase(2.0f)]
    [TestCase(5.0f)]
    public unsafe void LinearAllocation(float commitSizeAmplification)
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();
        var headerSize = sizeof(DefaultMemoryManager.SmallBlockAllocator.SegmentHeader);
        var allocSize = (16 + headerSize).Pad16() - headerSize;
        var seqSize = (int)(bs.DebugInfo.TotalCommitted * commitSizeAmplification);
        var marginSize = DefaultMemoryManager.BlockMarginSize;

        var segs = new List<(MemoryBlock, byte)>();
        byte curIndex = 1;
        var curTotalAllocated = 0;
        var curAllocSegCount = 0;
        while (seqSize > 0)
        {
            var s0 = mm.Allocate(allocSize);
            FillSegment(s0, curIndex);
            segs.Add((s0, curIndex));

            curTotalAllocated += allocSize + marginSize * 2;
            ++curAllocSegCount;

            var di = bs.DebugInfo;
            Assert.That(di.IsCoherent, Is.True, $"Seg Count: {curAllocSegCount}, Remaining Alloc Size: {seqSize}");
            Assert.That(di.TotalAllocatedMemory, Is.EqualTo(curTotalAllocated));
            Assert.That(di.AllocatedSegmentCount, Is.EqualTo(curAllocSegCount));

            ref var sh0 = ref mm.GetSegmentHeader(s0.MemorySegment);
            Assert.That(sh0.GenHeader.IsFree, Is.False);

            seqSize -= allocSize + headerSize;
            ++curIndex;
        }

        foreach (var tuple in segs)
        {
            var ms = tuple.Item1.Cast<byte>();
            var v = tuple.Item2;
            Assert.That(_masks[v].Check(ms), Is.True);
            Assert.That(mm.Free(tuple.Item1), Is.True);
        }

        Console.WriteLine($"Allocated {curAllocSegCount} segments, total size {curTotalAllocated}");
    }

    [Test]
    public unsafe void SmallAllocations()
    {
        const int start = 1;
        const int end = 32;
        const int count = end - start + 1;
        
        using var mm = new DefaultMemoryManager();

        var mbList = new MemoryBlock[end + 1];
        for (int i = start; i <= end; i++)
        {
            var memoryBlock = mm.Allocate(i);
            Assert.That(memoryBlock.MemorySegment.Length, Is.EqualTo(i));
            Assert.That(memoryBlock.MemoryManager, Is.EqualTo(mm));
            mbList[i] = memoryBlock;
        }
        
        for (int i = start; i <= end; i++)
        {
            mbList[i].Dispose();
        }
    }

    [Test]
    public void ZeroSizedAllocationSucceedButDontAllocSegment()
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();
        var seqCount = bs.DebugInfo.AllocatedSegmentCount;

        {
            using var mb = mm.Allocate(0);
            Assert.That(mb.MemorySegment.Length, Is.EqualTo(0));
            Assert.That(mb.IsDefault, Is.False);
            Assert.That(mb.IsDisposed, Is.False);
            Assert.That(mb.MemoryManager, Is.EqualTo(mm));
            Assert.That(seqCount, Is.EqualTo(bs.DebugInfo.AllocatedSegmentCount));
        }
    }
    
    [Test]
    [TestCase(0.5f)]
    [TestCase(1.0f)]
    [TestCase(2.0f)]
    [TestCase(5.0f)]
    public unsafe void LinearAllocation_then_intertwineReallocation(float commitSizeAmplification)
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();

        var headerSize = sizeof(DefaultMemoryManager.SmallBlockAllocator.SegmentHeader);
        var allocSize = (16 + headerSize).Pad16() - headerSize;
        var seqSize = (int)(bs.DebugInfo.TotalCommitted * commitSizeAmplification);

        var blocks = new List<MemoryBlock>();
        var curTotalAllocated = 0;
        var curAllocSegCount = 0;
        while (seqSize > 0)
        {
            var b = mm.Allocate(allocSize);
            Assert.That(((long)b.MemorySegment.Address & 0xF) == 0, Is.True);
            FillSegment(b, 1);

            blocks.Add(b);

            curTotalAllocated += allocSize;
            seqSize -= allocSize + headerSize;
            ++curAllocSegCount;
        }

        var maxTotalAllocated = curTotalAllocated;
        var maxAllocSegCount = curAllocSegCount;

        // Free all the even segments recorded
        for (var i = 0; i < blocks.Count; i++)
        {
            if ((curAllocSegCount & 1) == 0) continue;

            var b = blocks[i];
            mm.Free(b);

            var newBlock = mm.Allocate(allocSize);
            blocks[i] = newBlock;

            var di = bs.DebugInfo;
            Assert.That(di.IsCoherent, Is.True, $"Index: {i}");
        }

        Assert.That(curTotalAllocated, Is.EqualTo(maxTotalAllocated));
        Assert.That(curAllocSegCount, Is.EqualTo(maxAllocSegCount));

        foreach (var b in blocks)
        {
            mm.Free(b);
        }

        Console.WriteLine($"Allocated {curAllocSegCount} segments, total size {curTotalAllocated}");
    }

    [Test]
    public unsafe void DefragmentFreeSegmentTest()
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();

        var headerSize = sizeof(DefaultMemoryManager.SmallBlockAllocator.SegmentHeader);
        var allocSize = (16 + headerSize).Pad16() - headerSize;

        var di = bs.DebugInfo;
        Assert.That(di.IsCoherent, Is.True);
        var beforeFreeSegCount = di.FreeSegmentCount;

        bs.DefragmentFreeSegments();
        di = bs.DebugInfo;
        var actualFreeSegCount = di.FreeSegmentCount;
        Assert.That(actualFreeSegCount, Is.EqualTo(beforeFreeSegCount));

        var s0 = mm.Allocate(allocSize);
        FillSegment(s0, 1);

        var s1 = mm.Allocate(allocSize);
        FillSegment(s1, 2);

        var s2 = mm.Allocate(allocSize);
        FillSegment(s2, 3);

        mm.Free(s0);
        mm.Free(s2);
        mm.Free(s1);

        bs.DefragmentFreeSegments();

        di = bs.DebugInfo;
        Assert.That(di.IsCoherent, Is.True);
        actualFreeSegCount = di.FreeSegmentCount;

        Assert.That(actualFreeSegCount, Is.EqualTo(beforeFreeSegCount));
    }

    [Test]
    public unsafe void FastAllocations()
    {
        using var mm = new DefaultMemoryManager();
        var rand = new Random(123);

        var count = 1_000_000;
        if (OneTimeSetup.IsRunningUnderDotCover())
        {
            Console.WriteLine("DotCover detected, reducing op count to 1/10th of the original value.");
            count /= 10;
        }
        
        var list = new List<MemoryBlock>(count);

        var sw = new Stopwatch();
        sw.Start();

        for (int i = 0; i < count; i++)
        {
            var size = rand.Next(8, 128);
            var seg = mm.Allocate(size);
            list.Add(seg);
        }

        sw.Stop();

        var di = mm.GetThreadBlockAllocatorSequence().DebugInfo;
        Console.WriteLine($"Allocated {di.TotalAllocatedMemory.FriendlySize()} in {count.FriendlyAmount()} segments, in {sw.Elapsed.TotalSeconds.FriendlyTime(false)}");

        sw.Restart();
        for (int i = 0; i < count; i++)
        {
            mm.Free(list[i]);
        }
        sw.Stop();

        di = mm.GetThreadBlockAllocatorSequence().DebugInfo;
        Console.WriteLine($"Fee the {count.FriendlyAmount()} segments, in {sw.Elapsed.TotalSeconds.FriendlyTime(false)}");
    }
    [Test]
    [TestCase(0.5f, 32, 256 * 1024)]
    [TestCase(1.0f, 32, 256 * 1024)]
    [TestCase(2.0f, 32, 256 * 1024)]
    [TestCase(5.0f, 16, 64 * 1024)]
    [TestCase(0.5f, 512, 64 * 1024)]

    public void StressTest(float amp, int maxSegmentSize, int? opPerThread)
    {
        var cpuCount = (Environment.ProcessorCount * amp);
        opPerThread ??= 1024 * 1024;

        if (OneTimeSetup.IsRunningUnderDotCover())
        {
            Console.WriteLine("DotCover detected, reducing op count to 1/64th of the original value.");
            opPerThread >>= 6;
        }
        
        var allocTableSize = 1024 * 2;
        var bulkFreeCount = allocTableSize / 16;

        using var mm = new DefaultMemoryManager();

        var taskList = new List<Task>();
        for (var i = 0; i < cpuCount; i++)
        {
            var id = i;
            var t = Task.Run(() =>
            {
                var allocTable = new MemoryBlock[allocTableSize];
                var allocCount = 0;

                var sw = new Stopwatch();
                sw.Start();

                var bs = mm.GetThreadBlockAllocatorSequence();
                var rand = new Random(123 + id * 214);
                var max = opPerThread;

                var totalAllocated = 0L;
                var totalFree = 0L;
                var purgeCount = 0;
                var totalAllocTicks = 0L;
                var totalFreeTicks = 0L;
                var totalAllocCount = 0;
                var totalFreeCount = 0;

                for (var j = 0; j < max; j++)
                {
                    // Allocation
                    if (rand.Next(0, 16) < 10)
                    {
                        var size = rand.Next(8, maxSegmentSize);

                        var b = Stopwatch.GetTimestamp();
                        var block = mm.Allocate(size);
                        totalAllocTicks += Stopwatch.GetTimestamp() - b;
                        ++totalAllocCount; 

                        if (allocCount == allocTableSize)
                        {
                            // Free 16 block to make some room
                            allocCount = allocTableSize - bulkFreeCount;
                            for (var k = allocCount; k < allocTableSize; k++)
                            {
                                ref var s = ref allocTable[k];
                                var b1 = Stopwatch.GetTimestamp();
                                mm.Free(s);
                                totalFreeTicks += Stopwatch.GetTimestamp() - b1;
                                ++totalFreeCount;

                                totalFree += s.MemorySegment.Length;
                            }

                            ++purgeCount;
                        }

                        allocTable[allocCount++] = block;
                        totalAllocated += size;
                    }

                    // Free
                    else
                    {
                        if (allocCount > 0)
                        {
                            var index = rand.Next(0, allocCount);
                            if (index == allocCount - 1)
                            {
                                ref var s = ref allocTable[index];
                                var b1 = Stopwatch.GetTimestamp();
                                mm.Free(s);
                                totalFreeTicks += Stopwatch.GetTimestamp() - b1;
                                ++totalFreeCount;
                                --allocCount;
                                totalFree += s.MemorySegment.Length;
                            }
                            else
                            {
                                ref var s = ref allocTable[index];
                                var b1 = Stopwatch.GetTimestamp();
                                mm.Free(s);
                                totalFreeTicks += Stopwatch.GetTimestamp() - b1;
                                ++totalFreeCount;
                                allocTable[index] = allocTable[--allocCount];
                                totalFree += s.MemorySegment.Length;
                            }
                        }
                    }

                }

                sw.Stop();
                var di = bs.DebugInfo;
                Assert.That(di.IsCoherent, Is.True);
                Console.WriteLine($"Total Blocks: {di.TotalBlockCount}, Total Allocated {totalAllocated.FriendlySize()}, Total Free: {totalFree.FriendlySize()}, Scan Free List Count: {di.ScanFreeListCount.FriendlySize()}, Defrag count: {di.FreeSegmentDefragCount.FriendlySize()} in {opPerThread.Value.FriendlySize()} ops, in {sw.Elapsed.TotalSeconds.FriendlyTime(false)}");
                var freeTicks = Math.Max(1, (long)(totalFreeTicks / (double)totalFreeCount));
                Console.WriteLine($"Average Alloc Time {TimeSpan.FromTicks(totalAllocTicks / totalAllocCount).TotalSeconds.FriendlyTime(true)}, Average Free Time {TimeSpan.FromTicks(freeTicks).TotalSeconds.FriendlyTime(true)}, ");

                for (int j = 0; j < allocCount; j++)
                {
                    mm.Free(allocTable[j]);
                }
            });

            taskList.Add(t);
        }

        Task.WaitAll(taskList.ToArray());
    }

    [Test]
    [TestCase(0.5f)]
    [TestCase(1.0f)]
    [TestCase(2.0f)]
    [TestCase(5.0f)]
    public unsafe void LinearBigAllocation(float commitSizeAmplification)
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();
        var headerSize = sizeof(DefaultMemoryManager.LargeBlockAllocator.SegmentHeader);
        var allocSize = (65536*2) - headerSize;
        var seqSize = (int)(DefaultMemoryManager.LargeBlockMinSize * 16 * commitSizeAmplification);
        var marginSize = DefaultMemoryManager.BlockMarginSize;

        var blocks = new List<(MemoryBlock, byte)>();
        byte curIndex = 1;
        var curTotalAllocated = 0;
        var curAllocSegCount = 0;
        while (seqSize > 0)
        {
            var b0 = mm.Allocate(allocSize);
            FillSegment(b0, curIndex);
            blocks.Add((b0, curIndex));

            curTotalAllocated += allocSize + marginSize * 2;
            ++curAllocSegCount;

            var di = bs.DebugInfo;
            Assert.That(di.IsCoherent, Is.True, $"Seg Count: {curAllocSegCount}, Remaining Alloc Size: {seqSize}");
            Assert.That(di.TotalAllocatedMemory, Is.EqualTo(curTotalAllocated));
            Assert.That(di.AllocatedSegmentCount, Is.EqualTo(curAllocSegCount));

            seqSize -= allocSize + headerSize + marginSize * 2;
            ++curIndex;
        }

        foreach (var tuple in blocks)
        {
            var ms = tuple.Item1.MemorySegment.Cast<byte>();
            var v = tuple.Item2;
            Assert.That(_masks[v].Check(ms), Is.True);
        }

        foreach (var tuple in blocks)
        {
            mm.Free(tuple.Item1);
        }

        Console.WriteLine($"Allocated {curAllocSegCount} segments, total size {curTotalAllocated.FriendlySize()}, Native Blocks Count: {mm.NativeBlockCount} Total Size: {mm.NativeBlockTotalSize.FriendlySize()}");
    }

    unsafe struct TestS
    {
        fixed byte _process[16];
    }
    
    [Test]
    public void BigAllocTest()
    {
        using var mm = new DefaultMemoryManager();
        var size = DefaultMemoryManager.LargeBlockAllocator.SegmentHeader.MaxSegmentSize;

        var a = GC.AllocateUninitializedArray<TestS>(Array.MaxLength);
        
        var b = mm.Allocate(DefaultMemoryManager.MaxMemorySegmentSize);
    }
    
    [Test]
    public void BlockRecycleTest()
    {
        using var mm = new DefaultMemoryManager();
        var bs = mm.GetThreadBlockAllocatorSequence();

        var size = DefaultMemoryManager.MemorySegmentMaxSizeForSmallBlock;

        // Small Block test

        {
            var blocks = new List<MemoryBlock>();
            var block = mm.Allocate(size);
            blocks.Add(block);
            var di = bs.DebugInfo;
            var curBlockCount = di.TotalBlockCount;
            var count = 0;
            while (di.TotalBlockCount == curBlockCount)
            {
                block = mm.Allocate(size);
                blocks.Add(block);
                di = bs.DebugInfo;
                count++;
            }

            var last = blocks[^1];
            blocks.RemoveAt(blocks.Count - 1);
            mm.Free(last);

            di = bs.DebugInfo;
            Assert.That(di.TotalBlockCount, Is.EqualTo(curBlockCount));

            foreach (var b in blocks)
            {
                mm.Free(b);
            }
        }

        // Large Block test
        {
            var blocks = new List<MemoryBlock>();
            var block = mm.Allocate(size * 2);
            blocks.Add(block);
            var di = bs.DebugInfo;
            var count = 0;
            var curBlockCount = di.TotalBlockCount;
            while (di.TotalBlockCount == curBlockCount)
            {
                block = mm.Allocate(size * 2);
                blocks.Add(block);
                di = bs.DebugInfo;
                count++;
            }

            var last = blocks[^1];
            blocks.RemoveAt(blocks.Count - 1);
            mm.Free(last);

            di = bs.DebugInfo;
            Assert.That(di.TotalBlockCount, Is.EqualTo(curBlockCount));

            foreach (var b in blocks)
            {
                mm.Free(b);
            }
        }
    }

#if DEBUGALLOC
    [Test]
    public unsafe void BlockOverrunDetection()
    {
        using var mm = new DefaultMemoryManager(true);

        {
            var s0 = mm.Allocate(16);
            mm.Free(s0);
        }

        Assert.Throws<BlockOverrunException>(() =>
        {
            var s0 = mm.Allocate(16);

            var seg = new MemorySegment(s0.MemorySegment.Address - 20, s0.MemorySegment.Length + 20);
            seg.Cast<ushort>()[0] = 0x1234;

            mm.Free(s0);
        });

        Assert.Throws<BlockOverrunException>(() =>
        {
            var s0 = mm.Allocate(16);

            var seg = new MemorySegment(s0.MemorySegment.Address + 16 + 20, s0.MemorySegment.Length + 22);
            seg.Cast<ushort>()[0] = 0x1234;

            mm.Free(s0);
        });

    }
#endif
}