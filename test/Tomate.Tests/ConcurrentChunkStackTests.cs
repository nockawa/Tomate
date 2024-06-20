using NUnit.Framework;

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
            var queue = MappedConcurrentChunkBasedQueue.Create(seg);
            var queueBufferSize = queue.BufferSize;
            var startOffset = 0;
            
            // Dequeue the empty queue, handle should be default
            {
                using var h = queue.TryDequeue();
                Assert.That(h.IsDefault, Is.True);
            }

            // Enqueue 3 long
            {
                using var h = queue.Enqueue<long>(1, 3);
                startOffset += 3 * sizeof(long);
                h[0] = 123;
                h[1] = 456;
                h[2] = 789;
            }

            // Dequeue them
            {
                using var h = queue.TryDequeue();
                Assert.That(h.IsDefault, Is.False);

                var hl = h.ChunkData.Cast<long>();
                Assert.That(hl[0] == 123);
                Assert.That(hl[1] == 456);
                Assert.That(hl[2] == 789);
            }

            // Another dequeue, should be empty
            {
                using var h = queue.TryDequeue();
                Assert.That(h.IsDefault, Is.True);
            }

            // Enqueue 3 int
            {
                using var h = queue.Enqueue<int>(2, 3);
                startOffset += 3 * sizeof(int);
                h[0] = 777;
                h[1] = 888;
                h[2] = 999;
            }

            // Dequeue them
            {
                using var h = queue.TryDequeue();
                Assert.That(h.IsDefault, Is.False);

                var hl = h.ChunkData.Cast<int>();
                Assert.That(hl[0] == 777);
                Assert.That(hl[1] == 888);
                Assert.That(hl[2] == 999);
            }
            
            // Now enqueue a chunk that will wrap around the buffer
            var bigChunkLength = (ushort)((queueBufferSize - startOffset + 8) / 3);
            {
                for (int j = 0; j < 3; j++)
                {
                    {
                        using var h = queue.Enqueue<byte>((ushort)(4 + j), bigChunkLength);
                        startOffset += bigChunkLength;
                
                        for (int i = 0; i < bigChunkLength; i++)
                        {
                            h[i] = (byte)(i % 256);
                        }
                    }

                    {
                        using var h = queue.TryDequeue();
                        Assert.That(h.ChunkId, Is.EqualTo(4 + j));
                        Assert.That(h.IsDefault, Is.False);
                        Assert.That(h.ChunkData.Length, Is.EqualTo(bigChunkLength));
                
                        var hl = h.ChunkData.Cast<byte>();
                        for (int i = 0; i < hl.Length; i++)
                        {
                            Assert.That(hl[i] == (byte)(i % 256));
                        }
                    }
                }
            }
        }
        finally
        {
            _mm.Free(seg);
        }
    }
    
    [Test]
    [TestCase(512, 1, 1, 50_000, 50_000, 0)]
    [TestCase(10 * 1024, 2, 1, 1_000, 2_000, 0)]
    [TestCase(10 * 1024, 2, 2, 1_000, 1_000, 0)]
    [TestCase(10 * 1024, 2, 2, 10_000, 10_000, 0)]
    [TestCase(10 * 1024, 2, 4, 20_000, 10_000, 0)]
    [TestCase(10 * 1024, 4, 1, 20_000, 80_000, 0)]
    [TestCase(1 * 1024, 6, 1, 10_000, 60_000, 0)]
    [TestCase(10 * 1024, 1, 1, 100_000, 100_000, 1)]
    [TestCase(512, 2, 2, 1_000, 1_000, 0)]
    [TestCase(10 * 1024, 2, 2, 1_000, 1_000, 1)]
    [TestCase(10 * 1024, 2, 2, 10_000, 10_000, 1)]
    [TestCase(1024, 8, 1, 10_000, 80_000, 1)]
    public void MultithreadedTest(int bufferSize, int prodThreadCount, int consThreadCount, int prodOpCount, int consOpCount, int waitMs)
    {
        var taskList = new List<Task>(prodThreadCount + consThreadCount);
        //var logger = Log.ForContext<MappedConcurrentChunkBasedQueue>(); 

        if (OneTimeSetup.IsRunningUnderDotCover())
        {
            Console.WriteLine("DotCover detected, reducing op count to 1/10th of the original value.");
            prodOpCount /= 10;
            consOpCount /= 10;
        }
        
        var seg = _mm.Allocate(bufferSize);
        var queue = MappedConcurrentChunkBasedQueue.Create(seg);
        var rand = new Random(DateTime.UtcNow.Millisecond);
        TimeSpan? wait = waitMs == 0 ? null : TimeSpan.FromMilliseconds(waitMs);

        int chunkId = 0;

        // logger.Information("*** Starting test, buffer size {BufferSize} ***", queue.BufferSize);

        CancellationTokenSource cts = new();
        
        for (int i = 0; i < prodThreadCount; i++)
        {
            var i1 = i;
            taskList.Add(Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"*** Producer #{i1} ***";
                try
                {
                    for (int opCount = 0; opCount < prodOpCount; opCount++)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                    
                        var curChunkId = Interlocked.Increment(ref chunkId) % MappedConcurrentChunkBasedQueue.MaxChunkId;
                        if (curChunkId == 0)
                        {
                            curChunkId = Interlocked.Increment(ref chunkId) % MappedConcurrentChunkBasedQueue.MaxChunkId;
                        }
                    
                        var size = (ushort)rand.Next(2, 10);
                        var h = queue.Enqueue<int>((ushort)curChunkId, size, wait, cts.Token);
                        while (h.IsDefault)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            h.Dispose();
                            h = queue.Enqueue<int>((ushort)curChunkId, size, wait, cts.Token);
                        }
                        var s = rand.Next(0, 100000);
                        for (int j = 0; j < size; j++)
                        {
                            h[j] = s + j;
                        }
                        h.Dispose();
                        Thread.Sleep(0);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
                // logger.Information("Thread {CurrentThreadName} is done!", threadName);
            }, cts.Token));
        }

        for (int i = 0; i < consThreadCount; i++)
        {
            var i1 = i;
            taskList.Add(Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"*** Consumer #{i1} ***";
                try
                {
                    var spin = new SpinWait();
                    for (var opCount = 0; opCount < consOpCount; opCount++)
                    {
                        var h = queue.TryDequeue();
                        while (h.IsDefault)
                        {
                            spin.SpinOnce(0);
                            h.Dispose();
                            h = queue.TryDequeue();
                        }

                        var chunkData = h.ChunkData.Cast<int>();
                        Assert.That(chunkData.Length, Is.GreaterThanOrEqualTo(2));

                        var cur = chunkData[0];
                        int j;
                        for (j = 1; j < chunkData.Length; j++)
                        {
                            if (chunkData[j] != cur + j)
                            {
                                break;
                            }
                        }
                        Assert.That(j, Is.EqualTo(chunkData.Length));
                    
                        h.Dispose();
                        Thread.Sleep(0);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    cts.Cancel();
                    throw;
                }
                // logger.Information("Thread {CurrentThreadName} is done!", Thread.CurrentThread.Name);
            }, cts.Token));
        }

        Task.WaitAll(taskList.ToArray());

        _mm.Free(seg);
        Console.WriteLine("*** Test ended ***");
        // logger.Information("*** Test ended ***");
    }
}