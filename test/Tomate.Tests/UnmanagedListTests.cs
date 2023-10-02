using System.Diagnostics;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedListTests
{
    [Test]
    public unsafe void RemoveAtTest()
    {
        using var mm = new DefaultMemoryManager();
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
    }

    private static int GenList(int count, out long ts)
    {
        GC.Collect();

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

        GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }

    private static int GenUnmanagedList(DefaultMemoryManager mm, int count, out long ts)
    {
        ts = Stopwatch.GetTimestamp();
        var ul = new UnmanagedList<int>(mm);

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
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }
}