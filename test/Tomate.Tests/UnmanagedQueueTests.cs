using NUnit.Framework;

namespace Tomate.Tests;

public class UnmanagedQueueTests
{
    [Test]
    public void BasicTest()
    {
        var initialCapacity = 4;
        var totalItemCount = 10000;
        
        using var mm = new DefaultMemoryManager();
        using var uq = new UnmanagedQueue<int>(mm, initialCapacity);

        var q = new Queue<int>();
        for (int i = 0; i < 10; i++)
        {
            q.Enqueue(i);
        }
        
        // Enqueue items
        for (int i = 0; i < totalItemCount; i++)
        {
            uq.Enqueue(i);
            Assert.That(uq.Count, Is.EqualTo(i + 1));
            Assert.That(uq.Peek(), Is.EqualTo(0));
        }

        // Dequeue them all
        for (int i = 0; i < totalItemCount; i++)
        {
            ref var val = ref uq.Dequeue();
            Assert.That(val, Is.EqualTo(i));
            Assert.That(uq.Count, Is.EqualTo(totalItemCount - (i + 1)));
            if (i < (totalItemCount - 1))
            {
                Assert.That(uq.Peek(), Is.EqualTo(i + 1));
            }
        }

        Assert.That(uq.Count, Is.EqualTo(0));
        
        // Enqueue couple, dequeue them
        for (int i = 0; i < totalItemCount; i++)
        {
            uq.Enqueue(i);
            uq.Enqueue(i + 10);
            
            Assert.That(uq.Dequeue(), Is.EqualTo(i));
            Assert.That(uq.Dequeue(), Is.EqualTo(i + 10));
        }

        // Interleave queue/dequeue 2-1
        for (int i = 0; i < totalItemCount; i++)
        {
            uq.Enqueue(i * 2);
            uq.Enqueue(i * 2 + 1);
            Assert.That(uq.Dequeue(), Is.EqualTo(i));
        }
        Assert.That(uq.Count, Is.EqualTo(totalItemCount));
        for (int i = 0; i < totalItemCount; i++)
        {
            Assert.That(uq.Dequeue(), Is.EqualTo(i + totalItemCount));
        }
    }
}