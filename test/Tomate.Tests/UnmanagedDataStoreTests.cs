using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedDataStoreTests
{
    private PageAllocator _allocator;

    [SetUp]
    public void Setup()
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";
        _allocator = new PageAllocator(256 * 1024, 64);
    }

    [TearDown]
    public void TearDown()
    {
        _allocator.Dispose();
    }
    
    [Test]
    public void BasicTest()
    {
        using var udsSegment = DefaultMemoryManager.GlobalInstance.Allocate(UnmanagedDataStore.ComputeStorageSize(10));
        var uds = UnmanagedDataStore.Create(_allocator, udsSegment);
        var ec = uds.EntryCountPerPage * 10;

        var handles = new UnmanagedDataStore.Handle<UnmanagedList<int>>[ec];
        
        for (var i = 0; i < ec; i++)
        {
            using var ul = new UnmanagedList<int>();
            ul.Add(i);

            handles[i] = uds.Store(ul);
        }

        for (var i = 0; i < ec; i++)
        {
            ref var ul2 = ref uds.Get(handles[i]);
            Assert.That(ul2[0], Is.EqualTo(i));
        }
    }
    
    [Test]
    public void MaxCapacityReachedTest()
    {
        using var udsSegment = DefaultMemoryManager.GlobalInstance.Allocate(UnmanagedDataStore.ComputeStorageSize(1));
        var uds = UnmanagedDataStore.Create(_allocator, udsSegment);
        var ec = uds.EntryCountPerPage;

        var handles = new UnmanagedDataStore.Handle<UnmanagedList<int>>[ec];
        
        for (var i = 0; i < ec; i++)
        {
            using var ul = new UnmanagedList<int>();
            ul.Add(i);

            handles[i] = uds.Store(ul);
        }

        Assert.Throws<ItemMaxCapacityReachedException>(() =>
        {
            using var ul = new UnmanagedList<int>();
            uds.Store(ul);
        });
    }
    
    [Test]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void MultiThreadTest(int threadCount)
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";
        using var udsSegment = DefaultMemoryManager.GlobalInstance.Allocate(UnmanagedDataStore.ComputeStorageSize(10));
        var uds = UnmanagedDataStore.Create(_allocator, udsSegment);

        {
            var totalItems = uds.EntryCountPerPage;

            var taskList = new List<Task>(threadCount);
            for (int ti = 0; ti < threadCount; ti++)
            {
                var threadI = ti;
                taskList.Add(Task.Run(() =>
                {
                    Thread.CurrentThread.Name = $"*** Worker Thread #{threadI} ***";
                    var handleList = new UnmanagedDataStore.Handle<UnmanagedList<int>>[totalItems];
                    for (int i = 0, j = 0; i < totalItems; i++, j++)
                    {
                        using var ul = new UnmanagedList<int>();
                        var handle = uds.Store(ul);
                        handleList[j] = handle;

                        var id = uds.Get(handle);
                        for (var k = 0; k < 4; k++)
                        {
                            id.Add(j * 100 + k);
                        }
                    }

                    for (int i = 0; i < handleList.Length; i++)
                    {
                        var id = uds.Get(handleList[i]);
                        for (int k = 0; k < 4; k++)
                        {
                            Assert.That(id[k], Is.EqualTo(i * 100 + k));
                        }
                    }
                }));
            }
            Task.WaitAll(taskList.ToArray());
        }
    }
}