using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedDictionaryTests
{
    [Test]
    public void DictionaryTest()
    {
        using var mm = new DefaultMemoryManager();
        using var dic = new UnmanagedDictionary<int, int>(mm);

        for (int i = 0; i < 1000; i++)
        {
            dic.Add(i, i + (((i & 1) != 0) ? 50 : 0));
        }
        Assert.That(dic.Count, Is.EqualTo(1000));

        var enumCount = 0;
        foreach (ref var kvp in dic)
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
            ref var v = ref dic.GetOrAdd(i, out var found);
            Assert.That(found, Is.False);
            v = i;
        }
        Assert.That(dic.Count, Is.EqualTo(750));

        var h = new HashSet<int>();
        enumCount = 0;
        foreach (ref var kvp in dic)
        {
            ++enumCount;
            Assert.That(h.Add(kvp.Key), Is.True, $"Key {kvp.Key}, Value {kvp.Value}");
            kvp.Value += 10;
        }
        Assert.That(enumCount, Is.EqualTo(750));

        enumCount = 0;
        foreach (ref var kvp in dic)
        {
            ++enumCount;
            Assert.That(kvp.Key + 10, Is.EqualTo(kvp.Value));
        }
        Assert.That(enumCount, Is.EqualTo(750));
    }
}