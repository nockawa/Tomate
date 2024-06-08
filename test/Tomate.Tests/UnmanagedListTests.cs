using System.Diagnostics;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedListTests
{
    [Test]
    public void AddTest()
    {
        using var ul = new UnmanagedList<int>();
        
        // Pre check
        Assert.That(ul.Count, Is.EqualTo(0));
        
        // Add one item
        ul.Add(10);
        Assert.That(ul.Count, Is.EqualTo(1));
        Assert.That(ul[0], Is.EqualTo(10));
        
        // Add a second
        ul.Add(20);
        Assert.That(ul.Count, Is.EqualTo(2));
        Assert.That(ul[1], Is.EqualTo(20));
        
        // and a third
        ul.Add(30);
        Assert.That(ul.Count, Is.EqualTo(3));
        Assert.That(ul[2], Is.EqualTo(30));
        
        // Add in place
        ref var i4 = ref ul.AddInPlace();
        i4 = 40;
        Assert.That(ul.Count, Is.EqualTo(4));
        Assert.That(ul[3], Is.EqualTo(40));

        // Change item directly from the ref
        i4 = 50;
        Assert.That(ul[3], Is.EqualTo(50));

        // Set with subscript
        ul[3] = 60;
        Assert.That(ul[3], Is.EqualTo(60));
        
        // Bunch of AddInPlace triggering resize
        var curIndex = ul.Count;
        for (int i = 0; i < 32; i++)
        {
            ref var aip = ref ul.AddInPlace();
            aip = i;
        }
        for (int i = 0; i < 32; i++)
        {
            Assert.That(ul[curIndex + i], Is.EqualTo(i));
        }
    }

    [Test]
    public void AccessorAddTest()
    {
        using var ul = new UnmanagedList<int>();
        var acc = ul.FastAccessor;
        
        // Pre check
        Assert.That(acc.Count, Is.EqualTo(0));
        
        // Add one item
        acc.Add(10);
        Assert.That(acc.Count, Is.EqualTo(1));
        Assert.That(acc[0], Is.EqualTo(10));
        
        // Add a second
        acc.Add(20);
        Assert.That(acc.Count, Is.EqualTo(2));
        Assert.That(acc[1], Is.EqualTo(20));
        
        // and a third
        acc.Add(30);
        Assert.That(acc.Count, Is.EqualTo(3));
        Assert.That(acc[2], Is.EqualTo(30));
        
        // Add in place
        ref var i4 = ref acc.AddInPlace();
        i4 = 40;
        Assert.That(acc.Count, Is.EqualTo(4));
        Assert.That(acc[3], Is.EqualTo(40));

        // Change item directly from the ref
        i4 = 50;
        Assert.That(acc[3], Is.EqualTo(50));

        // Set with subscript
        acc[3] = 60;
        Assert.That(acc[3], Is.EqualTo(60));
        
        // Bunch of AddInPlace triggering resize
        var curIndex = acc.Count;
        for (int i = 0; i < 32; i++)
        {
            ref var aip = ref acc.AddInPlace();
            aip = i;
        }
        for (int i = 0; i < 32; i++)
        {
            Assert.That(acc[curIndex + i], Is.EqualTo(i));
        }
    }
    
    [Test]
    public void OperationOnInvalidInstanceTest()
    {
        {
            // Operation on default instance
            UnmanagedList<int> def = default;
            Assert.Throws<ObjectDisposedException>(() => def.Add(10));
            Assert.Throws<ObjectDisposedException>(() => def.AddInPlace());
            Assert.Throws<ObjectDisposedException>(() => def.RemoveAt(0));
            Assert.Throws<ObjectDisposedException>(() => def.Remove(0));
            Assert.Throws<ObjectDisposedException>(() => def.Insert(0, 0));
            Assert.Throws<ObjectDisposedException>(() =>
            {
                var _ = def.Content;
            });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                var _ = def.FastAccessor.Content;
            });
            Assert.Throws<ObjectDisposedException>(() => def.FastAccessor.Add(10));
            Assert.Throws<ObjectDisposedException>(() =>
            {
                var v = def[10];
            });
            Assert.Throws<ObjectDisposedException>(() => def.Remove(0));
            Assert.That(def.Capacity, Is.EqualTo(-1));
            Assert.That(def.RefCounter, Is.EqualTo(-1));
        }
        {
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                using var ul = new UnmanagedList<int>(DefaultMemoryManager.GlobalInstance, -10);
            });
        }
    }

    [Test]
    public void InvalidOperationsTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                var v = ul[-1];
            });
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                var v = ul.FastAccessor[-1];
            });
            
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                ul.Insert(-1, 0);
            });
            
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                ul.FastAccessor.Insert(-1, 0);
            });
            
        }
    }

    [Test]
    public unsafe void OverallocationTest()
    {
        var aa = GC.AllocateUninitializedArray<long>(1 * 1024 * 1024 * 1024);
        
        var max = DefaultMemoryManager.GlobalInstance.MaxAllocationLength / sizeof(Guid);
        {
            Assert.Throws<InvalidAllocationSizeException>(() =>
            {
                var ul = new UnmanagedList<Guid>(DefaultMemoryManager.GlobalInstance, max + 1);
            });
        }
        {
            var ul = new UnmanagedList<Guid>(DefaultMemoryManager.GlobalInstance, 32);
            Assert.Throws<InvalidAllocationSizeException>(() =>
            {
                ul.Capacity = max + 1;
            });
            
            ul.Dispose();
        }
        {
            var initialCapacity = max / 2 + 1;
            var ul = new UnmanagedList<Guid>(DefaultMemoryManager.GlobalInstance, initialCapacity);

            {
                var acc = ul.FastAccessor;
                for (int i = 0; i < initialCapacity; i++)
                {
                    acc.Add(Guid.Empty);
                }
            }

            Assert.Throws<InvalidAllocationSizeException>(() =>
            {
                ul.Add(Guid.NewGuid());
            });

        }
    }
    
    [Test]
    public void CreateWithGivenCapacityTest()
    {
        {
            using var ul = new UnmanagedList<int>(DefaultMemoryManager.GlobalInstance, 23);
            Assert.That(ul.Capacity, Is.EqualTo(23));
            Assert.That(ul.FastAccessor.Capacity, Is.EqualTo(23));
        }
    }

    [Test]
    public void CantResizeLessThanItemCount()
    {
        {
            var ul = new UnmanagedList<int>(DefaultMemoryManager.GlobalInstance, 8);
            for (int i = 0; i < 6; i++)
            {
                ul.Add(i);
            }
        
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                ul.Capacity = 5;
            });

            ul.Capacity = 32;
            Assert.That(ul.Capacity, Is.EqualTo(32));
            Assert.That(ul.Count, Is.EqualTo(6));
            for (int i = 0; i < 6; i++)
            {
                Assert.That(ul[i], Is.EqualTo(i));
            }
            ul.Dispose();
        }
        {
            using var ul = new UnmanagedList<int>(DefaultMemoryManager.GlobalInstance, 8);
            var acc = ul.FastAccessor;
            for (int i = 0; i < 6; i++)
            {
                acc.Add(i);
            }

            var success = false;
            try
            {
                acc.Capacity = 5;
            }
            catch (IndexOutOfRangeException)
            {
                success = true;
            }
            Assert.That(success, Is.True);

            acc.Capacity = 32;
            Assert.That(acc.Capacity, Is.EqualTo(32));
            Assert.That(acc.Count, Is.EqualTo(6));
            for (int i = 0; i < 6; i++)
            {
                Assert.That(acc[i], Is.EqualTo(i));
            }
        }
    }
    
    [Test]
    public void DoubleDisposeHarmlessTest()
    {
        {
            // Operation on default instance
            UnmanagedList<int> def = default;
            def.Dispose();
        }

        {
            using var ul = new UnmanagedList<int>();
            Assert.That(ul.IsDisposed, Is.EqualTo(false));
            ul.Add(10);
            
            ul.Dispose();
            Assert.That(ul.IsDisposed, Is.EqualTo(true));
            
            // Second dispose should be harmless
            ul.Dispose();
            Assert.That(ul.IsDisposed, Is.EqualTo(true));
            
        }
    }

    [Test]
    public void CopyToTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(i);
            }

            var arr = new int[16];
            {
                ul.CopyTo(arr, 0);
                for (int i = 0; i < 8; i++)
                {
                    Assert.That(arr[i], Is.EqualTo(i));
                }
                ul.CopyTo(arr, 8);
                for (int i = 0; i < 8; i++)
                {
                    Assert.That(arr[8+i], Is.EqualTo(i));
                }
            }
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                acc.Add(i);
            }

            var arr = new int[16];
            {
                acc.CopyTo(arr, 0);
                for (int i = 0; i < 8; i++)
                {
                    Assert.That(arr[i], Is.EqualTo(i));
                }
                acc.CopyTo(arr, 8);
                for (int i = 0; i < 8; i++)
                {
                    Assert.That(arr[8+i], Is.EqualTo(i));
                }
            }
        }
    }

    [Test]
    public void InsertTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(i * 2);
            }
            
            ul.Insert(3, 30);
            Assert.That(ul.Count, Is.EqualTo(9));
            Assert.That(ul[3], Is.EqualTo(30));
            Assert.That(ul[4], Is.EqualTo(6));
            
            ul.Insert(9, 90);
            Assert.That(ul.Count, Is.EqualTo(10));
            Assert.That(ul[9], Is.EqualTo(90));
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                acc.Add(i * 2);
            }
            
            acc.Insert(3, 30);
            Assert.That(acc.Count, Is.EqualTo(9));
            Assert.That(acc[3], Is.EqualTo(30));
            Assert.That(acc[4], Is.EqualTo(6));
            
            acc.Insert(9, 90);
            Assert.That(acc.Count, Is.EqualTo(10));
            Assert.That(acc[9], Is.EqualTo(90));
        }
    }
    
    [Test]
    public void RemoveAtTest()
    {
        using var mm = new DefaultMemoryManager();
        {
            using var ul = new UnmanagedList<int>();
            ul.Add(10);
            ul.Add(11);
            ul.Add(12);
            ul.Add(13);
            ul.Add(14);
            ul.Add(15);

            ul.RemoveAt(2);

            Assert.That(ul.Count, Is.EqualTo(5));
            Assert.That(ul[0], Is.EqualTo(10));
            Assert.That(ul[1], Is.EqualTo(11));
            Assert.That(ul[2], Is.EqualTo(13));
            Assert.That(ul[3], Is.EqualTo(14));
            Assert.That(ul[4], Is.EqualTo(15));

            Assert.Throws<IndexOutOfRangeException>(() => ul.RemoveAt(-10));
        
            var i = ul.IndexOf(14);
            Assert.That(i, Is.EqualTo(3));

            i = 0;
            var expectedNumbers = new[] { 10, 11, 13, 14, 15 }; 
            foreach (var item in ul)
            {
                Assert.That(item, Is.EqualTo(expectedNumbers[i++]));
            }
        
            ul.Clear();
            foreach (var item in ul)
            {
                Assert.Fail("There should be no item to enumerate");
            }
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            acc.Add(10);
            acc.Add(11);
            acc.Add(12);
            acc.Add(13);
            acc.Add(14);
            acc.Add(15);

            acc.RemoveAt(2);

            Assert.That(acc.Count, Is.EqualTo(5));
            Assert.That(acc[0], Is.EqualTo(10));
            Assert.That(acc[1], Is.EqualTo(11));
            Assert.That(acc[2], Is.EqualTo(13));
            Assert.That(acc[3], Is.EqualTo(14));
            Assert.That(acc[4], Is.EqualTo(15));

            Assert.Throws<IndexOutOfRangeException>(() => ul.FastAccessor.RemoveAt(-10));
        
            var i = acc.IndexOf(14);
            Assert.That(i, Is.EqualTo(3));

            i = 0;
            var expectedNumbers = new[] { 10, 11, 13, 14, 15 }; 
            foreach (var item in acc)
            {
                Assert.That(item, Is.EqualTo(expectedNumbers[i++]));
            }
        
            acc.Clear();
            foreach (var item in acc)
            {
                Assert.Fail("There should be no item to enumerate");
            }
        }
    }

    [Test]
    public void RemoveTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(i);
            }

            ul.Remove(4);
            Assert.That(ul.Count, Is.EqualTo(7));
            Assert.That(ul[4], Is.EqualTo(5));
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                acc.Add(i);
            }

            acc.Remove(4);
            Assert.That(acc.Count, Is.EqualTo(7));
            Assert.That(acc[4], Is.EqualTo(5));
        }
    }

    [Test]
    public void ContentTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            for (int i = 0; i < 16; i++)
            {
                ul.Add(i * 2);
            }

            var content = ul.Content;
            for (int i = 0; i < 16; i++)
            {
                Assert.That(content[i], Is.EqualTo(i * 2));
            }
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 16; i++)
            {
                acc.Add(i * 2);
            }

            var content = acc.Content;
            for (int i = 0; i < 16; i++)
            {
                Assert.That(content[i], Is.EqualTo(i * 2));
            }
        }
    }
    
    private struct TestStruct
    {
        public TestStruct()
        {
            A = 0;
            B = 0;
        }

        public TestStruct(int a, int b)
        {
            A = a;
            B = b;
        }

        public int A;
        public int B;
    }

    [Test]
    public void IndexOfTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(i);
            }
            
            Assert.That(ul.IndexOf(3), Is.EqualTo(3));
            Assert.That(ul.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<int>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                acc.Add(i);
            }
            
            Assert.That(acc.IndexOf(3), Is.EqualTo(3));
            Assert.That(acc.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<long>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(i);
            }
            
            Assert.That(ul.IndexOf(3), Is.EqualTo(3));
            Assert.That(ul.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<long>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                acc.Add(i);
            }
            
            Assert.That(acc.IndexOf(3), Is.EqualTo(3));
            Assert.That(acc.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<short>();
            for (short i = 0; i < 8; i++)
            {
                ul.Add(i);
            }
            
            Assert.That(ul.IndexOf(3), Is.EqualTo(3));
            Assert.That(ul.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<short>();
            var acc = ul.FastAccessor;
            for (short i = 0; i < 8; i++)
            {
                acc.Add(i);
            }
            
            Assert.That(acc.IndexOf(3), Is.EqualTo(3));
            Assert.That(acc.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<byte>();
            for (byte i = 0; i < 8; i++)
            {
                ul.Add(i);
            }
            
            Assert.That(ul.IndexOf(3), Is.EqualTo(3));
            Assert.That(ul.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<byte>();
            var acc = ul.FastAccessor;
            for (byte i = 0; i < 8; i++)
            {
                acc.Add(i);
            }
            
            Assert.That(acc.IndexOf(3), Is.EqualTo(3));
            Assert.That(acc.IndexOf(7), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<TestStruct>();
            for (int i = 0; i < 8; i++)
            {
                ul.Add(new TestStruct(i, i * 2));
            }
            
            Assert.That(ul.IndexOf(new TestStruct(3, 6)), Is.EqualTo(3));
            Assert.That(ul.IndexOf(new TestStruct(7, 14)), Is.EqualTo(7));
        }
        {
            using var ul = new UnmanagedList<TestStruct>();
            var acc = ul.FastAccessor;
            for (int i = 0; i < 8; i++)
            {
                ul.Add(new TestStruct(i, i * 2));
            }
            
            Assert.That(ul.IndexOf(new TestStruct(3, 6)), Is.EqualTo(3));
            Assert.That(ul.IndexOf(new TestStruct(7, 14)), Is.EqualTo(7));
        }
    }

    [Test]
    public void UnderlyingMemoryBlockTest()
    {
        {
            using var ul = new UnmanagedList<int>();
            Assert.That(ul.MemoryManager, Is.EqualTo(DefaultMemoryManager.GlobalInstance));
            Assert.That(ul.MemoryBlock.RefCounter, Is.EqualTo(1));

            ul.AddRef();
            Assert.That(ul.MemoryBlock.RefCounter, Is.EqualTo(2));
            
            // ReSharper disable once DisposeOnUsingVariable
            ul.Dispose();
            Assert.That(ul.MemoryBlock.RefCounter, Is.EqualTo(1));
        }
    }

    [Test]
    public void PerfTest()
    {
        var count = 256 * 1024;

        // List test
        for (int i = 0; i < 10; i++)
        {
            var v = GenList(count, out var ts);
            Console.WriteLine($"Create, fill, release List<int> with {v} items, took {TimeSpan.FromTicks(ts).TotalSeconds.FriendlyTime()}");

        }

        // UnmanagedList test
        var mm = new DefaultMemoryManager();

        for (int i = 0; i < 10; i++)
        {
            var v = GenUnmanagedList(mm, count, out var ts);
            Console.WriteLine($"Create, fill, release UnmanagedList<int> with {v} items, took {TimeSpan.FromTicks(ts).TotalSeconds.FriendlyTime()}");
        }

        for (int i = 0; i < 10; i++)
        {
            var v = GenUnmanagedListFast(mm, count, out var ts);
            Console.WriteLine($"Create, fill, release UnmanagedList<int> with {v} items, FAST, took {TimeSpan.FromTicks(ts).TotalSeconds.FriendlyTime()}");
        }
    }

    private static int GenList(int count, out long ts)
    {
        //GC.Collect();

        ts = Stopwatch.GetTimestamp();
        var list = new List<int>();
        for (int i = 0; i < count; i += 8)
        {
            list.Add(i);
            list.Add(i);
            list.Add(i);
            list.Add(i);
            list.Add(i);
            list.Add(i);
            list.Add(i);
            list.Add(i);
        }

        var v = list.Count;

        //GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }

    private static int GenUnmanagedList(DefaultMemoryManager mm, int count, out long ts)
    {
        //GC.Collect();

        ts = Stopwatch.GetTimestamp();
        using var ul = new UnmanagedList<int>(mm);

        for (int i = 0; i < count; i+=8)
        {
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
            ul.Add(i);
        }

        var v = ul.Count;

        ul.Dispose();
        //GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }

    private static int GenUnmanagedListFast(DefaultMemoryManager mm, int count, out long ts)
    {
        //GC.Collect();

        ts = Stopwatch.GetTimestamp();
        using var ul = new UnmanagedList<int>(mm);
        var accessor = ul.FastAccessor;

        for (int i = 0; i < count; i+=8)
        {
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
            accessor.Add(i);
        }

        var v = ul.Count;

        ul.Dispose();
        //GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }
}