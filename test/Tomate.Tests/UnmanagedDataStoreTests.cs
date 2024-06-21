using System.Collections.Concurrent;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedDataStoreTests
{
    [Test]
    public void BasicTest()
    {
        var maxCount = 1024;

        using var allocator = new DefaultMemoryManager(4 * 1024, 1024 * 256);
        var (size, levels) = UnmanagedDataStore.ComputeStorageSize(allocator, maxCount);
        using var udsSegment = allocator.Allocate(size);
        var uds = UnmanagedDataStore.Create(allocator, udsSegment, levels);
        
        var handles = new UnmanagedDataStore.Handle<UnmanagedList<int>>[maxCount];
        
        for (var i = 0; i < maxCount; i++)
        {
            var ul = new UnmanagedList<int>();
            ul.Add(i * 10);
        
            handles[i] = uds.Store(ref ul);
            
            ul.Dispose();
        }

        for (var i = 0; i < maxCount; i++)
        {
            ref var ul2 = ref uds.Get(handles[i]);
            Assert.That(ul2[0], Is.EqualTo(i * 10));

            // Trigger a resize of the list's content
            ul2.Add(i * 10 + 1);
            ul2.Add(i * 10 + 2);
            ul2.Add(i * 10 + 3);
            ul2.Add(i * 10 + 4);
            ul2.Add(i * 10 + 5);
        }

        // Parse again, we should be able to read the proper content of the resized instances
        for (var i = 0; i < maxCount; i++)
        {
            ref var ul2 = ref uds.Get(handles[i]);
            
            Assert.That(ul2[0], Is.EqualTo(i * 10 + 0));
            Assert.That(ul2[1], Is.EqualTo(i * 10 + 1));
            Assert.That(ul2[2], Is.EqualTo(i * 10 + 2));
            Assert.That(ul2[3], Is.EqualTo(i * 10 + 3));
            Assert.That(ul2[4], Is.EqualTo(i * 10 + 4));
            Assert.That(ul2[5], Is.EqualTo(i * 10 + 5));
            
            // Release the list
            uds.Remove(handles[i]);
        }
    }

    [Test]
    [TestCase(4 * 1024, 1024 * 256, 2)]
    [TestCase(4 * 1024, 1024 * 256, 4)]
    [TestCase(4 * 1024, 1024 * 256, 8)]
    [TestCase(1024 * 1024, 1024, 2)]
    [TestCase(1024 * 1024, 1024, 4)]
    [TestCase(1024 * 1024, 1024, 8)]
    public void MultiThreadTest(int allocatorPageSize, int allocatorPageCount, int threadCount)
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";

        using var allocator = new DefaultMemoryManager(allocatorPageSize, allocatorPageCount);
        
        var totalItems = 16 * 1024;

        var (size, levels) = UnmanagedDataStore.ComputeStorageSize(allocator, totalItems);
        using var udsSegment = allocator.Allocate(size);
        var uds = UnmanagedDataStore.Create(allocator, udsSegment, levels);

        var bag = new ConcurrentBag<UnmanagedDataStore.Handle<UnmanagedList<int>>>();
        var random = new Random(DateTime.UtcNow.Millisecond);

        var threadOpCount = totalItems * 16 / threadCount;
        Console.WriteLine($"Test with {threadCount} threads, {threadOpCount.FriendlyAmount()} operations each. Total Ops: {(threadCount*threadOpCount).FriendlyAmount()}.");

        void PopCheck(UnmanagedDataStore.Handle<UnmanagedList<int>> handle)
        {
            ref var ul = ref uds.Get(handle);
            var handleIndex = handle.Index;
            Assert.That(ul.Count, Is.EqualTo(4));
            for (int k = 0; k < 4; k++)
            {
                Assert.That(ul[k], Is.EqualTo(handleIndex * 10 + k));
            }

            // Free the list
            Assert.That(uds.Remove(handle), Is.True);
        }
        
        // Use x thread to either push a new list or pop an existing one, check it and release it
        var taskList = new List<Task>(threadCount);
        for (int ti = 0; ti < threadCount; ti++)
        {
            var threadI = ti;
            taskList.Add(Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"*** Worker Thread #{threadI} ***";
                var safetyLimit = (totalItems - 128);

                for (int i = 0; i < threadOpCount; i++)
                {
                    Retry:
                    // Check if we are getting close to the limit, pop one item
                    if (bag.Count > safetyLimit)
                    {
                        if (bag.TryTake(out var handle))
                        {
                            PopCheck(handle);
                        }
                        else
                        {
                            goto Retry;
                        }
                    }
                    
                    // Push if there's no item or if there's still room left and hazard decided it
                    else if (bag.IsEmpty || (bag.Count < safetyLimit && random.Next(0, 100) < 50))
                    {
                        UnmanagedDataStore.Handle<UnmanagedList<int>> handle;
                        {
                            var ul = new UnmanagedList<int>();
                            handle = uds.Store(ref ul);
                            ul.Dispose();
                        }
                        {
                            ref var ul = ref uds.Get(handle);
                            var handleIndex = handle.Index;
                            for (var k = 0; k < 4; k++)
                            {
                                ul.Add(handleIndex * 10 + k);
                            }
                        }
                        bag.Add(handle);
                    }
                    
                    // Pop
                    else
                    {
                        if (bag.TryTake(out var handle))
                        {
                            PopCheck(handle);
                        }
                        else
                        {
                            goto Retry;
                        }
                    }
                }
            }));
        }
        Task.WaitAll(taskList.ToArray());
    }    
    

    [Test]
    public void MaxCapacityReachedTest()
    {
        using var allocator = new DefaultMemoryManager(4 * 1024, 1024 * 256);
        var ec = 16 * 1024;
        var (size, levels) = UnmanagedDataStore.ComputeStorageSize(allocator, ec);
        using var udsSegment = allocator.Allocate(size);
        var uds = UnmanagedDataStore.Create(allocator, udsSegment, levels);
        ec = uds.GetMaxEntryCount<UnmanagedDictionary<int, int>>();
        
        var handles = new UnmanagedDataStore.Handle<UnmanagedDictionary<int, int>>[ec];
        
        for (var i = 0; i < ec; i++)
        {
            var ud = new UnmanagedDictionary<int, int>();
            ud.Add(i, i);
    
            handles[i] = uds.Store(ref ud);
            ud.Dispose();
        }
    
        Assert.Throws<ItemMaxCapacityReachedException>(() =>
        {
            var ul = new UnmanagedDictionary<int, int>();
            uds.Store(ref ul);
        });
    }

    [Test]
    public void StoreDisposeFreesCollections()
    {
        using var allocator = new DefaultMemoryManager(4 * 1024, 1024 * 256);
        var ec = 16 * 1024;
        var (size, levels) = UnmanagedDataStore.ComputeStorageSize(allocator, ec);
        using var udsSegment = allocator.Allocate(size);
        var uds = UnmanagedDataStore.Create(allocator, udsSegment, levels);
        
        var handles = new UnmanagedDataStore.Handle<UnmanagedList<int>>[10];

        ref var l0 = ref UnmanagedList<int>.CreateInStore(allocator, uds, 4, out handles[0]);
        ref var l1 = ref UnmanagedList<int>.CreateInStore(allocator, uds, 4, out handles[1]);
        ref var l2 = ref UnmanagedList<int>.CreateInStore(allocator, uds, 4, out handles[2]);
        ref var l3 = ref UnmanagedList<int>.CreateInStore(allocator, uds, 4, out handles[3]);

        var l2MemoryBlock = l2.MemoryBlock;
        l2.AddRef();
        
        uds.Dispose();
        
        Assert.That(l0.MemoryBlock.IsDefault, Is.True);
        Assert.That(l1.MemoryBlock.IsDefault, Is.True);
        Assert.That(l2MemoryBlock.IsDefault, Is.False);
        Assert.That(l3.MemoryBlock.IsDefault, Is.True);
        
        l2.Dispose();
        
    }
}