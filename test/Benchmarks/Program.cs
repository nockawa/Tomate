using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Tomate;

namespace Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<Benchmark>();
    }
}


[SimpleJob(RunStrategy.Throughput, RuntimeMoniker.Net60, 1, 2, 5)]
public unsafe class Benchmark
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

    [Benchmark(Baseline = true, OperationsPerInvoke  = 1)]
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

    [Benchmark(OperationsPerInvoke  = 1)]
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

    [Benchmark(OperationsPerInvoke  = 1)]
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