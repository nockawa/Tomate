using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Tomate.Tests;

public class ConcurrentChunkStackTests
{
    private DefaultMemoryManager _mm;
    
    [SetUp]
    public void Setup()
    {
        _mm = new DefaultMemoryManager();
    }


    [Test]
    public void SingleThreadTest()
    {
        var seg = _mm.Allocate(1024);
        try
        {
            var stack = MappedConcurrentChunkQueue.Create(seg);

            {
                using var h = stack.TryDequeue();
                Assert.That(h.IsDefault, Is.True);
            }

            {
                using var h = stack.Enqueue<long>(1, 3);
                h[0] = 123;
                h[1] = 456;
                h[2] = 789;
            }

            {
                using var h = stack.TryDequeue();
                Assert.That(h.IsDefault, Is.False);

                var hl = h.MemorySegment.Cast<long>();
                Assert.That(hl[0] == 123);
                Assert.That(hl[1] == 456);
                Assert.That(hl[2] == 789);
            }

            {
                using var h = stack.TryDequeue();
                Assert.That(h.IsDefault, Is.True);
            }
        }
        finally
        {
            _mm.Free(seg);
        }
    }

    [Test]
    [TestCase(10 * 1024, 1, 1, 100_000, 100_000, 0)]
    [TestCase(10 * 1024, 2, 1, 1_000, 2_000, 0)]
    [TestCase(10 * 1024, 2, 2, 1_000, 1_000, 0)]
    [TestCase(10 * 1024, 2, 2, 10_000, 10_000, 0)]
    [TestCase(10 * 1024, 2, 4, 20_000, 10_000, 0)]
    [TestCase(10 * 1024, 4, 1, 20_000, 80_000, 0)]
    [TestCase(1 * 1240, 6, 1, 10_000, 60_000, 0)]
    [TestCase(10 * 1024, 1, 1, 100_000, 100_000, 1)]
    [TestCase(10 * 1024, 2, 1, 1_000, 2_000, 1)]
    [TestCase(10 * 1024, 2, 2, 1_000, 1_000, 1)]
    [TestCase(10 * 1024, 2, 2, 10_000, 10_000, 1)]
    [TestCase(1024, 8, 1, 10_000, 80_000, 1)]
    public void MultithreadedTest(int bufferSize, int prodThreadCount, int consThreadCount, int prodOpCount, int consOpCount, int waitMs)
    {
        var taskList = new List<Task>(prodThreadCount + consThreadCount);

        var seg = _mm.Allocate(bufferSize);
        var stack = MappedConcurrentChunkQueue.Create(seg);
        var rand = new Random(DateTime.UtcNow.Millisecond);
        TimeSpan? wait = waitMs == 0 ? null : TimeSpan.FromMilliseconds(waitMs);

        for (int i = 0; i < prodThreadCount; i++)
        {
            var i1 = i;
            taskList.Add(Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"*** Producer #{i1} ***";
                for (int opCount = 0; opCount < prodOpCount && TestContext.CurrentContext.Result.Outcome.Status==TestStatus.Inconclusive; opCount++)
                {
                    var size = (ushort)rand.Next(1, 10);
                    var h = stack.Enqueue<int>(1, size, wait);
                    while (h.IsDefault)
                    {
                        h.Dispose();
                        h = stack.Enqueue<int>(1, size, wait);
                    }
                    var s = rand.Next(0, 100000);
                    for (int j = 0; j < size; j++)
                    {
                        h[j] = s + j;
                    }
                    h.Dispose();
                }
            }));
        }

        for (int i = 0; i < consThreadCount; i++)
        {
            var i1 = i;
            taskList.Add(Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"*** Consumer #{i1} ***";
                var spin = new SpinWait();
                for (int opCount = 0; opCount < consOpCount && TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Inconclusive; opCount++)
                {
                    var h = stack.TryDequeue();
                    while (h.IsDefault)
                    {
                        spin.SpinOnce(0);
                        h.Dispose();
                        h = stack.TryDequeue();
                    }

                    Assert.That(h.ChunkId, Is.EqualTo(1));
                    var seg = h.MemorySegment.Cast<int>();
                    var cur = seg[0];
                    for (int j = 1; j < seg.Length; j++)
                    {
                        var next = seg[j];
                        Assert.That(cur + 1, Is.EqualTo(next));
                        cur = next;
                    }
                    h.Dispose();
                }
                Console.WriteLine($"Thread {Thread.CurrentThread.Name} is done!");
            }));
        }

        Task.WaitAll(taskList.ToArray());

        _mm.Free(seg);
    }
}