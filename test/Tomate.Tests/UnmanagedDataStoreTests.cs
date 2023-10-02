using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedDataStoreTests
{
    private PageAllocator _allocator;
    UnmanagedDataStore<long> _uds;

    [SetUp]
    public void Setup()
    {
        _allocator = new PageAllocator(256 * 1024, 64);
        _uds = UnmanagedDataStore<long>.Create(_allocator);
    }

    [TearDown]
    public void TearDown()
    {
        _uds.Dispose();
        _allocator.Dispose();
    }
    
    [Test]
    public void BasicTest()
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";

        var itemPack = 4;
        var itemPerPage = (_uds.ItemCountPerPage/itemPack)*itemPack;
        var totalItems = (int)(itemPerPage * 0.5f);
        var indexList = new int[totalItems / itemPack];
        for (int i = 0, j = 0; i < totalItems; i+=itemPack, j++)
        {
            var index = _uds.Allocate(itemPack);
            Assert.That(index, Is.GreaterThanOrEqualTo(i));
            indexList[j] = index;

            var id = _uds.GetItem(index, itemPack);
            for (var k = 0; k < itemPack; k++)
            {
                id[k] = j * 100 + k;
            }
        }

        for (int i = 0; i < indexList.Length; i++)
        {
            var id = _uds.GetItem(indexList[i], itemPack);
            for (int k = 0; k < itemPack; k++)
            {
                Assert.That(id[k], Is.EqualTo(i * 100 + k));
            }
        }
    }
    
    [Test]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void MultiThreadTest(int threadCount)
    {
        Thread.CurrentThread.Name = $"*** Unit Test Exec Thread ***";

        {
            var itemPack = 4;
            var itemPerPage = (_uds.ItemCountPerPage/itemPack)*itemPack;
            var totalItems = (int)(itemPerPage * 0.5f);

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
                        var index = _uds.Allocate(itemPack);
                        Assert.That(index, Is.GreaterThanOrEqualTo(i));
                        indexList[j] = index;

                        var id = _uds.GetItem(index, itemPack);
                        for (var k = 0; k < itemPack; k++)
                        {
                            id[k] = j * 100 + k;
                        }
                    }

                    for (int i = 0; i < indexList.Length; i++)
                    {
                        var id = _uds.GetItem(indexList[i], itemPack);
                        for (int k = 0; k < itemPack; k++)
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