using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;

namespace Tomate.Tests;

public class ConcurrentBitmapL3Tests
{
    private const int bitLength = 1024 * 1024;

    private MemoryManager _mm;
    private ConcurrentBitmapL4 _bitmap;

    [SetUp]
    public void Setup()
    {
        _mm = new MemoryManager(64 * 1024 * 1024);
        var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(bitLength);
        var seg = _mm.Allocate(requireSize);
        _bitmap = ConcurrentBitmapL4.Create(bitLength, seg);
    }

    [Test]
    public void TestDefautKeyNotAllowed()
    {
        var res = new List<(int, int)>(1024 * 256);

        for (int i = 0; i < (1024 * 256); i++)
        {
            res.Add((_bitmap.AllocateBits((i % 4) + 1), (i % 4) + 1));
        }

        if (_bitmap.SanityCheck(out var error) == false)
        {
            Console.WriteLine(error);
        }

        for (int i = 0; i < 1024 * 256; i += 3)
        {
            var r = res[i];
            _bitmap.FreeBits(r.Item1, r.Item2);
        }
        
        if (_bitmap.SanityCheck(out error) == false)
        {
            Console.WriteLine(error);
        }

        Console.WriteLine($"Requests: {_bitmap.LookupCount}, Total iteration {_bitmap.LookupIterationCount}, iteration per request: {_bitmap.LookupIterationCount/(double)_bitmap.LookupCount}");
    }
}