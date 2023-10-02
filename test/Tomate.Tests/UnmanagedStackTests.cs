using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedStackTests
{
    [Test]
    public void BasicTest()
    {
        using var mm = new DefaultMemoryManager();
        using var us = new UnmanagedStack<int>(mm, 4);

        var count = 10000;
        for (int i = 0; i < count; i++)
        {
            us.Push(i);
            Assert.That(us.Count, Is.EqualTo(i + 1));
            Assert.That(us.Peek(), Is.EqualTo(i));
        }

        for (int i = count - 1; i >= 0; i--)
        {
            var val = us.Pop();
            Assert.That(val, Is.EqualTo(i));
            Assert.That(us.Count, Is.EqualTo(i));
        }
    }
}