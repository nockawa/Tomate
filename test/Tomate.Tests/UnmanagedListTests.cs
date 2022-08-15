using System.Diagnostics;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedListTests
{
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
        var v = 0;

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

        v = list.Count;

        GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }

    private static int GenUnmanagedList(DefaultMemoryManager mm, int count, out long ts)
    {
        ts = Stopwatch.GetTimestamp();
        var ul = new UnmanagedList<int>(mm);
        var v = 0;

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

        v = ul.Count;

        ul.Dispose();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }
}