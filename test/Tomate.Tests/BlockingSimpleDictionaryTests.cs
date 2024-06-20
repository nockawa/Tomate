using NUnit.Framework;

namespace Tomate.Tests;

public class BlockingSimpleDictionaryTests
{
    private DefaultMemoryManager _mm;

    [SetUp]
    public void Setup()
    {
        _mm = new DefaultMemoryManager();
    }

    [Test]
    public void TestDefautKeyNotAllowed()
    {
        var seg = _mm.Allocate(1024);
        var dic = MappedBlockingSimpleDictionary<int, int>.Create(seg);

        Assert.Throws<ArgumentException>(() => dic.TryAdd(default, 123));
        Assert.Throws<ArgumentException>(() => dic.GetOrAdd(default, _ => 123, out _, out _));

        _mm.Free(seg);
    }

    [Test]
    public void TestCommonOperations()
    {
        var seg = _mm.Allocate(1024);
        var dic = MappedBlockingSimpleDictionary<int, int>.Create(seg);

        Assert.That(dic.Count, Is.EqualTo(0));

        // Add new
        Assert.That(dic.TryAdd(123, 456), Is.True);
        Assert.That(dic.Count, Is.EqualTo(1));

        // Add second new
        Assert.That(dic.TryAdd(12, 564), Is.True);
        Assert.That(dic.Count, Is.EqualTo(2));

        // Get on non-existent
        Assert.That(dic.TryGet(124, out _), Is.False);

        // Get second
        Assert.That(dic.TryGet(12, out var v), Is.True);
        Assert.That(v, Is.EqualTo(564));

        // Get first
        Assert.That(dic.TryGet(123, out v), Is.True);
        Assert.That(v, Is.EqualTo(456));

        // Remove non-existent
        Assert.That(dic.TryRemove(124, out _), Is.False);
        Assert.That(dic.Count, Is.EqualTo(2));

        // Remove first
        Assert.That(dic.TryRemove(123, out _), Is.True);
        Assert.That(dic.Count, Is.EqualTo(1));

        // Remove second
        Assert.That(dic.TryRemove(12, out _), Is.True);
        Assert.That(dic.Count, Is.EqualTo(0));

        // GetOrAdd new
        Assert.That(dic.GetOrAdd(123, i => 999, out var a, out var s), Is.EqualTo(999));
        Assert.That(a, Is.True);
        Assert.That(s, Is.True);

        // GetOrAdd existing
        Assert.That(dic.GetOrAdd(123, i => 666, out a, out s), Is.EqualTo(999));
        Assert.That(a, Is.False);
        Assert.That(s, Is.True);

        // Clear
        dic.Clear();
        Assert.That(dic.Count, Is.EqualTo(0));

        // Capacity reach tests
        var capacity = dic.Capacity;
        for (int i = 0; i < capacity; i++)
        {
            Assert.That(dic.TryAdd(i + 12, i + 123), Is.True);
        }
        Assert.That(dic.TryAdd(666, 999), Is.False);

        _mm.Free(seg);
    }

    [Test]
    public void TestEnumeration()
    {
        var seg = _mm.Allocate(1024);
        var dic = MappedBlockingSimpleDictionary<int, int>.Create(seg);

        dic.TryAdd(12, 122);
        dic.TryAdd(13, 123);
        dic.TryAdd(14, 124);

        var it = 0;
        foreach (var kvp in dic)
        {
            switch (it)
            {
                case 0:
                    Assert.That(kvp.Key, Is.EqualTo(12));
                    break;
                case 1:
                    Assert.That(kvp.Key, Is.EqualTo(13));
                    break;
                case 2:
                    Assert.That(kvp.Key, Is.EqualTo(14));
                    break;
            }
            ++it;
        }
        Assert.That(it, Is.EqualTo(3));

        dic.TryRemove(13, out _);
        it = 0;
        foreach (var kvp in dic)
        {
            switch (it)
            {
                case 0:
                    Assert.That(kvp.Key, Is.EqualTo(12));
                    break;
                case 1:
                    Assert.That(kvp.Key, Is.EqualTo(14));
                    break;
            }
            ++it;
        }
        Assert.That(it, Is.EqualTo(2));

        _mm.Free(seg);
    }

    [Test]
    public void TestSubscriptOperator()
    {
        var seg = _mm.Allocate(1024);
        var dic = MappedBlockingSimpleDictionary<int, int>.Create(seg);

        dic[12] = 1;
        dic[15] = 2;
        dic[13] = 3;
        dic[16] = 4;
        dic[14] = 5;
        Assert.That(dic.Count, Is.EqualTo(5));

        Assert.That(dic[12], Is.EqualTo(1));
        Assert.That(dic[13], Is.EqualTo(3));
        Assert.That(dic[14], Is.EqualTo(5));
        Assert.That(dic[15], Is.EqualTo(2));
        Assert.That(dic[16], Is.EqualTo(4));

        dic[13] = 13;
        dic[16] = 14;
        Assert.That(dic.Count, Is.EqualTo(5));
        Assert.That(dic[13], Is.EqualTo(13));
        Assert.That(dic[16], Is.EqualTo(14));

        _mm.Free(seg);
    }
}