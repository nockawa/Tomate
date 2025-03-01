using System.Diagnostics;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedDictionaryTests
{
    [Test]
    public void DictionaryTest()
    {
        using var mm = new DefaultMemoryManager();
        using var dic = UnmanagedDictionary<int, int>.Create(mm);

        for (int i = 0; i < 1000; i++)
        {
            dic.Add(i, i + (((i & 1) != 0) ? 50 : 0));
        }
        Assert.That(dic.Count, Is.EqualTo(1000));

        var enumCount = 0;
        foreach (var kvp in dic)
        {
            ++enumCount;
            var isOdd = (kvp.Key & 1) != 0;
            Assert.That(kvp.Key, Is.EqualTo(kvp.Value - (isOdd ? 50 : 0)), $"Error at key: {kvp.Key}");
        }
        Assert.That(enumCount, Is.EqualTo(1000));

        for (int i = 1; i < 1000; i += 2)
        {
            dic.Remove(i, out _);
        }
        Assert.That(dic.Count, Is.EqualTo(500));

        for (int i = 1; i < 500; i += 2)
        {
            dic.GetOrAdd(i, out var found);
            Assert.That(found, Is.False);
            dic.TrySetValue(i, i);
        }
        Assert.That(dic.Count, Is.EqualTo(750));

        var h = new HashSet<int>();
        enumCount = 0;
        foreach (var kvp in dic)
        {
            ++enumCount;
            Assert.That(h.Add(kvp.Key), Is.True, $"Key {kvp.Key}, Value {kvp.Value}");
            dic.TrySetValue(kvp.Key, kvp.Value + 10);
        }
        Assert.That(enumCount, Is.EqualTo(750));

        enumCount = 0;
        foreach (var kvp in dic)
        {
            ++enumCount;
            Assert.That(kvp.Key + 10, Is.EqualTo(kvp.Value));
        }
        Assert.That(enumCount, Is.EqualTo(750));
    }

    // Highly inefficient comparer, just for testing
    public class String64IgnoreCaseComparer : IEqualityComparer<String64>
    {
        public bool Equals(String64 x, String64 y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x.AsString, y.AsString);
        }

        public int GetHashCode(String64 obj) => obj.AsString.ToUpper().GetHashCode();
        
        public static readonly String64IgnoreCaseComparer Default = new();
    }

    [Test]
    public void CustomComparerTest()
    {
        var s = Unsafe.SizeOf<UnmanagedDictionary<String64, int>>();
        
        using var mm = new DefaultMemoryManager();
        using var dic = UnmanagedDictionary<String64, int>.Create(mm);

        dic.Add($"Key1", 1, String64IgnoreCaseComparer.Default);
        dic.Add($"Key2", 2, String64IgnoreCaseComparer.Default);
        dic.Add($"Key10", 10, String64IgnoreCaseComparer.Default);
        
        Assert.That(dic.Count, Is.EqualTo(3));

        ref var val = ref dic.TryGetValue("KEY1", out var res, String64IgnoreCaseComparer.Default);
        Assert.That(res, Is.True);
    }
        
    [Test]
    public void PerfTest()
    {
        var count = 256 * 1024;

        // Dictionary test
        for (int i = 0; i < 10; i++)
        {
            var v = GenDictionary(count, out var ts);
            Console.WriteLine($"Create, fill, release Dictionary<int> with {v} items, took {TimeSpan.FromTicks(ts).TotalSeconds.FriendlyTime()}");

        }

        // UnmanagedDictionary test
        var mm = new DefaultMemoryManager();

        for (int i = 0; i < 10; i++)
        {
            var v = GenUnmanagedDictionary(mm, count, out var ts);
            Console.WriteLine($"Create, fill, release UnmanagedDictionary<int> with {v} items, took {TimeSpan.FromTicks(ts).TotalSeconds.FriendlyTime()}");
        }
    }

    private static int GenDictionary(int count, out long ts)
    {
        GC.Collect();

        ts = Stopwatch.GetTimestamp();
        var dic = new Dictionary<int, int>();
        for (int i = 0; i < count; i += 8)
        {
            dic.Add(i+0, i);
            dic.Add(i+1, i);
            dic.Add(i+2, i);
            dic.Add(i+3, i);
            dic.Add(i+4, i);
            dic.Add(i+5, i);
            dic.Add(i+6, i);
            dic.Add(i+7, i);
        }

        var v = dic.Count;

        GC.Collect();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }

    private static int GenUnmanagedDictionary(DefaultMemoryManager mm, int count, out long ts)
    {
        ts = Stopwatch.GetTimestamp();
        var ud = UnmanagedDictionary<int, int>.Create(mm);

        for (int i = 0; i < count; i+=8)
        {
            ud.Add(i+0, i);
            ud.Add(i+1, i);
            ud.Add(i+2, i);
            ud.Add(i+3, i);
            ud.Add(i+4, i);
            ud.Add(i+5, i);
            ud.Add(i+6, i);
            ud.Add(i+7, i);
        }

        var v = ud.Count;

        ud.Dispose();
        ts = Stopwatch.GetTimestamp() - ts;
        return v;
    }
}