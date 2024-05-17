using NUnit.Framework;

namespace Tomate.Tests;

public class ConcurrentBitmapL4Tests
{
    private DefaultMemoryManager _mm;

    [SetUp]
    public void Setup()
    {
        _mm = new DefaultMemoryManager();
    }

    [Test]
    public void TestDefaultKeyNotAllowed()
    {
        const int bitLength = 1024 * 1024;
        var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(bitLength);
        using var seg = _mm.Allocate(requireSize);
        var bitmap = ConcurrentBitmapL4.Create(bitLength, seg);

        var res = new List<(int, int)>(1024 * 256);

        for (var i = 0; i < (1024 * 256); i++)
        {
            res.Add((bitmap.AllocateBits((i % 4) + 1), (i % 4) + 1));
        }

        if (bitmap.SanityCheck(out var error) == false)
        {
            Console.WriteLine(error);
        }

        for (var i = 0; i < 1024 * 256; i += 3)
        {
            var r = res[i];
            bitmap.FreeBits(r.Item1, r.Item2);
        }
        
        if (bitmap.SanityCheck(out error) == false)
        {
            Console.WriteLine(error);
        }

        Console.WriteLine($"Requests: {bitmap.LookupCount}, Total iteration {bitmap.LookupIterationCount}, iteration per request: {bitmap.LookupIterationCount/(double)bitmap.LookupCount}");
    }

    [Test]
    [TestCase(1, 1)]
    [TestCase(1, 2)]
    [TestCase(1, 4)]
    [TestCase(2, 1)]
    [TestCase(2, 2)]
    [TestCase(2, 4)]
    [TestCase(3, 1)]
    [TestCase(3, 2)]
    [TestCase(3, 4)]
    [TestCase(4, 1)]
    [TestCase(4, 2)]
    [TestCase(4, 4)]
    [TestCase(6, 1)]
    [TestCase(6, 2)]
    [TestCase(6, 4)]
    public void MultithreadingTest(int itemPack, int threadCount)
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";
        
        int bitLength = 1024 * 1024;
        for (int k = 0; k < 3; k++)
        {
            var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(bitLength - k);
            using var seg = _mm.Allocate(requireSize);
            var bitmap = ConcurrentBitmapL4.Create(bitLength - k, seg);
            Assert.That(bitmap.SanityCheck(out var error), Is.True, error);

            {
                var totalItems = bitmap.Capacity / 16 / itemPack * itemPack;
                var taskList = new List<Task>(threadCount);
                for (int ti = 0; ti < threadCount; ti++)
                {
                    var threadI = ti;
                    taskList.Add(Task.Run(() =>
                    {
                        Thread.CurrentThread.Name = $"*** Worker Thread #{threadI} ***";
                        var indexList = new int[totalItems / itemPack];
                        for (int i = 0, j = 0; i < totalItems; i+=itemPack, j++)
                        {
                            var index = bitmap.AllocateBits(itemPack);
                            //Assert.That(bitmap.SanityCheck(out var lerror), Is.True, lerror);
                            indexList[j] = index;
                        }

                    }));
                }
                Task.WaitAll(taskList.ToArray());
            
                Assert.That(bitmap.SanityCheck(out error), Is.True, error);
            }
        }
        
    }

    [Test]
    [TestCase(1024)]
    [TestCase(1024 * 1024)]
    [TestCase(16 * 1024 * 1024)]
    public void AllocUntilFull(int capacity)
    {
        var requireSize = ConcurrentBitmapL4.ComputeRequiredSize(capacity);
        using var seg = _mm.Allocate(requireSize);
        var bitmap = ConcurrentBitmapL4.Create(capacity, seg);

        var allocPack = 8;
        for (int i = 0; i < capacity; i+=allocPack)
        {
            var index = bitmap.AllocateBits(allocPack);
            Assert.That(index, Is.Not.EqualTo(-1));
        }

        {
            var index = bitmap.AllocateBits(allocPack);
            Assert.That(index, Is.EqualTo(-1));
            
        }        
    }
    
}
