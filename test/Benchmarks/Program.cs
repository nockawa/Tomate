using System.Numerics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Tomate;

namespace Benchmarks;

public class Program
{
    public static unsafe void Main(string[] args)
    {
        //var b = new BitBenchark();

        //for (int i = 0; i < 256; i++)
        //{
        //    b.GlobalSetup();
        //    b.BenchL1();
        //    b.GlobalCleanup();
        //}

        //BenchmarkRunner.Run<BenchmarkSegmentAccess>();
        BenchmarkRunner.Run<BitBenchark>();
    }
}

public class TestClass
{
    public int A;
    public int B;

    public TestClass(int a, int b)
    {
        A = a;
        B = b;
    }
}

[SimpleJob(RunStrategy.Throughput, RuntimeMoniker.Net60, 1, 2, 5)]
[DisassemblyDiagnoser(printSource: true)]
[MemoryDiagnoser]
public unsafe class BitBenchark
{
    private const int bitLength = 1024 * 1024;
    private const int innerSize = 1024;
    private MemoryManager _mm;
    private ConcurrentBitmapL4 _bitmap;

    private TestClass[] _gcAlloc;

    [GlobalSetup]
    public void GlobalSetup()
    {

        //_val = new ulong[innerSize];

        //var rnd = new Random(DateTime.UtcNow.Millisecond);
        //for (int i = 0; i < innerSize; i++)
        //{
        //    _val[i] = (ulong)rnd.NextInt64();
        //}

        _mm = new MemoryManager(256 * 1024 * 1024);
        _gcAlloc = new TestClass[1024 * 128];
    }
    
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _mm.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 1024 * 128)]
    public int BenchL1()
    {
        var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(bitLength);
        var seg = _mm.Allocate(requireSize);
        _bitmap = ConcurrentBitmapL4.Create(bitLength, seg);
        for (int i = 0; i < (1024 * 16); i++)
        {
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
        }

        return 0;
    }
    [Benchmark(OperationsPerInvoke = 1024 * 128)]
    public int BenchL2()
    {
        var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(bitLength);
        var seg = _mm.Allocate(requireSize);
        _bitmap = ConcurrentBitmapL4.Create(bitLength, seg);
        for (int i = 0; i < (1024 * 16); i++)
        {
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
            _bitmap.AllocateBits(2);
        }

        return 0;
    }
    [Benchmark(OperationsPerInvoke = 1024 * 64)]
    public int BenchGCAlloc()
    {
        {
            var gca = new TestClass[1024 * 128];
            for (int i = 0; i < (1024 * 128); i+=8)
            {
                gca[i] = new TestClass(i, 12);

                if (i % 3 == 0) _gcAlloc[i] = gca[i];
            }
        }
        GC.Collect();
        return 0;
    }
}

[SimpleJob(RunStrategy.Throughput, RuntimeMoniker.Net60, 1, 2, 5)]
public unsafe class InterlockedBenchark
{
    private int[] _values;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _values = new int[1024];
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 50)]
    public void BenchRaw()
    {
        for (int i = 0; i < 1024; i++)
        {
            for (int j = 0; j < 1_000_00; j += 10)
            {
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
                ++_values[i];
            }
        }
    }
    [Benchmark(OperationsPerInvoke = 50)]
    public void BenchInterlockedInc()
    {
        var val = _values;

        for (int i = 0; i < 1024; i++)
        {
            for (int j = 0; j < 1_000_00; j += 10)
            {
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
                Interlocked.Increment(ref _values[i]);
            }
        }

    }

    [Benchmark(OperationsPerInvoke = 50)]
    public void BenchInterlockedCompXChange()
    {
        var val = _values;

        for (int i = 0; i < 1024; i++)
        {
            for (int j = 0; j < 1_000_00; j += 10)
            {
                Interlocked.CompareExchange(ref _values[i], j + 1, j);
                Interlocked.CompareExchange(ref _values[i], j + 2, j + 1);
                Interlocked.CompareExchange(ref _values[i], j + 3, j + 2);
                Interlocked.CompareExchange(ref _values[i], j + 4, j + 3);
                Interlocked.CompareExchange(ref _values[i], j + 5, j + 4);
                Interlocked.CompareExchange(ref _values[i], j + 6, j + 5);
                Interlocked.CompareExchange(ref _values[i], j + 7, j + 6);
                Interlocked.CompareExchange(ref _values[i], j + 8, j + 7);
                Interlocked.CompareExchange(ref _values[i], j + 9, j + 8);
                Interlocked.CompareExchange(ref _values[i], j + 10, j + 9);
            }
        }
    }
}

[SimpleJob(RunStrategy.Throughput, RuntimeMoniker.Net60, 1, 2, 5)]
public unsafe class BenchmarkSegmentAccess
{
    const int BlockSize = 64;

    private MemoryManager _mm;
    private byte* _mbRaw;
    private MemorySegment _mb;
    private LogicalMemorySegment _lb;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _mm = new MemoryManager(1 * 1024 * 1024 * 1024);
        _mbRaw = _mm.Allocate(BlockSize).Address;
        _mb = new MemorySegment(_mbRaw, BlockSize);
        _lb = new LogicalMemorySegment(0, BlockSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _mm.Dispose();
        _mm = null;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 1)]
    public long BenchRaw()
    {
        var addr = (long*)_mbRaw;

        var itCount = BlockSize / 8;
        long res = 0;
        for (int i = 0; i < itCount; i++)
        {
            res += addr[i];
        }

        return res;
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public long BenchMemBlockSpan()
    {
        var span = _mb.ToSpan<long>();
        var itCount = BlockSize / 8;
        long res = 0;
        for (int i = 0; i < itCount; i++)
        {
            res += span[i];
        }

        return res;
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public long BenchLogicalBlockSpan()
    {
        var span = _lb.ToSpan<long>(_mbRaw);
        var itCount = BlockSize / 8;
        long res = 0;
        for (int i = 0; i < itCount; i++)
        {
            res += span[i];
        }

        return res;
    }

}