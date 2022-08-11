using NUnit.Framework;

namespace Tomate.Tests;

public class TwoWaysLinkedListTests
{
    public struct Node
    {
        public TwoWaysLinkedList<int>.Link Link;
        public int Val;
    }

    [Test]
    public void ForwardTest()
    {
        var storage = new Node[256];
        var curIndex = 1;

        var ll = new TwoWaysLinkedList<int>(() => curIndex++, id => ref storage[id].Link);

        for (var i = 0; i < 32; i++)
        {
            var id = ll.InsertNewLast();
            storage[id].Val = i;
        }

        var curId = ll.FirstId;
        for (var i = 0; i < 32; i++)
        {
            var node = storage[curId];
            Assert.That(node.Val, Is.EqualTo(i));

            curId = node.Link.Next;
        }

        curIndex = 1;
        var count = ll.Walk(nodeId =>
        {
            Assert.That(nodeId, Is.EqualTo(curIndex++));
            return true;
        });
        Assert.That(count, Is.EqualTo(32));
    }

    [Test]
    public void InsertTest()
    {
        var storage = new Node[256];
        var curIndex = 1;

        var ll = new TwoWaysLinkedList<int>(() => curIndex++, id => ref storage[id].Link);

        // a
        var a = ll.InsertNew(default);
        storage[a].Val = 1;
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(1));
        Assert.That(ll.FirstId, Is.EqualTo(a));
        Assert.That(ll.LastId, Is.EqualTo(a));
        
        // a - c
        var c = ll.InsertNew(a);
        storage[c].Val = 3;
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(c));
        Assert.That(ll.Previous(c), Is.EqualTo(a));
        Assert.That(ll.Next(c), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(2));
        Assert.That(ll.FirstId, Is.EqualTo(a));
        Assert.That(ll.LastId, Is.EqualTo(c));

        // a - b - c
        var b = ll.InsertNew(a);
        storage[b].Val = 2;
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(b));
        Assert.That(ll.Previous(b), Is.EqualTo(a));
        Assert.That(ll.Next(b), Is.EqualTo(c));
        Assert.That(ll.Previous(c), Is.EqualTo(b));
        Assert.That(ll.Next(c), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(3));

        // a - b - c - d
        var d = ll.InsertNew(c);
        storage[d].Val = 4;
        Assert.That(ll.Previous(c), Is.EqualTo(b));
        Assert.That(ll.Next(c), Is.EqualTo(d));
        Assert.That(ll.Previous(d), Is.EqualTo(c));
        Assert.That(ll.Next(d), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(4));
        Assert.That(ll.FirstId, Is.EqualTo(a));
        Assert.That(ll.LastId, Is.EqualTo(d));

        // e - a - b- c- d
        var e = ll.InsertNewFirst();
        storage[e].Val = 5;
        Assert.That(ll.Previous(e), Is.EqualTo(0));
        Assert.That(ll.Next(e), Is.EqualTo(a));
        Assert.That(ll.Previous(a), Is.EqualTo(e));
        Assert.That(ll.Count, Is.EqualTo(5));
        Assert.That(ll.FirstId, Is.EqualTo(e));
        Assert.That(ll.LastId, Is.EqualTo(d));

        Assert.That(storage[a].Val, Is.EqualTo(1));
        Assert.That(storage[b].Val, Is.EqualTo(2));
        Assert.That(storage[c].Val, Is.EqualTo(3));
        Assert.That(storage[d].Val, Is.EqualTo(4));
        Assert.That(storage[e].Val, Is.EqualTo(5));
    }

    [Test]
    public void InsertLast()
    {
        var storage = new Node[256];
        var curIndex = 1;

        var ll = new TwoWaysLinkedList<int>(() => curIndex++, id => ref storage[id].Link);

        // a
        var a = ll.InsertNewLast();
        storage[a].Val = 1;
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(1));
        Assert.That(ll.FirstId, Is.EqualTo(a));
        Assert.That(ll.LastId, Is.EqualTo(a));

        // a - b
        var b = ll.InsertNewLast();
        storage[b].Val = 2;
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(b));
        Assert.That(ll.Previous(b), Is.EqualTo(a));
        Assert.That(ll.Next(b), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(2));
        Assert.That(ll.FirstId, Is.EqualTo(a));
        Assert.That(ll.LastId, Is.EqualTo(b));

        Assert.That(storage[a].Val, Is.EqualTo(1));
        Assert.That(storage[b].Val, Is.EqualTo(2));

    }

    [Test]
    public void RemoveTest()
    {
        var storage = new Node[256];
        var curIndex = 1;

        var ll = new TwoWaysLinkedList<int>(() => curIndex++, id => ref storage[id].Link);

        var a = ll.InsertNewLast();
        storage[a].Val = 1;
        var b = ll.InsertNewLast();
        storage[b].Val = 2;
        var c = ll.InsertNewLast();
        storage[c].Val = 3;
        var d = ll.InsertNewLast();
        storage[d].Val = 4;
        var e = ll.InsertNewLast();
        storage[e].Val = 5;

        // a - b - c - d - e
        ll.Remove(b);
        Assert.That(ll.Previous(a), Is.EqualTo(0));
        Assert.That(ll.Next(a), Is.EqualTo(c));
        Assert.That(ll.Previous(c), Is.EqualTo(a));
        Assert.That(ll.Count, Is.EqualTo(4));

        // a - c- d - e
        ll.Remove(e);
        Assert.That(ll.Next(d), Is.EqualTo(0));
        Assert.That(ll.LastId, Is.EqualTo(d));
        Assert.That(ll.Count, Is.EqualTo(3));

        // a - c - d
        ll.Remove(c);
        Assert.That(ll.Next(a), Is.EqualTo(d));
        Assert.That(ll.Previous(d), Is.EqualTo(a));
        Assert.That(ll.Count, Is.EqualTo(2));

        // a - d
        ll.Remove(a);
        Assert.That(ll.FirstId, Is.EqualTo(d));
        Assert.That(ll.Previous(d), Is.EqualTo(0));
        Assert.That(ll.Next(d), Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(1));

        // d
        ll.Remove(d);
        Assert.That(ll.FirstId, Is.EqualTo(0));
        Assert.That(ll.Count, Is.EqualTo(0));
    }
}