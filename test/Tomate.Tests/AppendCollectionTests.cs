using NUnit.Framework;

namespace Tomate.Tests;

public class AppendCollectionTests
{
    private PageAllocator _allocator;

    [SetUp]
    public void Setup()
    {
        _allocator = new PageAllocator(4 * 1024, 1024 * 64);
    }

    [Test]
    public void AllocationTest()
    {
        using var col = AppendCollection<long>.Create(_allocator, 16);

        for (int i = 0; i < 488; i += 4)
        {
            col.Reserve(4, out _).ToSpan().Fill(i);
        }

        col.Reserve(8, out var sid).ToSpan().Fill(502);

        for (int i = 0; i < 488; i+=4)
        {
            var seg = col.Get(i, 4);
            Assert.That(seg[0], Is.EqualTo(i));
            Assert.That(seg[1], Is.EqualTo(i));
            Assert.That(seg[2], Is.EqualTo(i));
            Assert.That(seg[3], Is.EqualTo(i));
        }

        {
            var seg = col.Get(sid, 8);
            Assert.That(seg[0], Is.EqualTo(502));
            Assert.That(seg[7], Is.EqualTo(502));
        }

        {
            using var col2 = AppendCollection<long>.Map(_allocator, col.RootPageId);

            for (int i = 0; i < 488; i += 4)
            {
                var seg = col2.Get(i, 4);
                Assert.That(seg[0], Is.EqualTo(i));
                Assert.That(seg[1], Is.EqualTo(i));
                Assert.That(seg[2], Is.EqualTo(i));
                Assert.That(seg[3], Is.EqualTo(i));
            }

            {
                var seg = col2.Get(sid, 8);
                Assert.That(seg[0], Is.EqualTo(502));
                Assert.That(seg[7], Is.EqualTo(502));
            }
        }

    }

    [Test]
    public void StringTableTest()
    {
        using var col = StringTable.Create(_allocator, 16);

        col.AddString("Pipo");
        col.AddString("Pouet Tagada");


    }
}