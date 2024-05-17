using NUnit.Framework;

namespace Tomate.Tests;

public class BitmapHelpersTests
{
    [Test]
    public void FindFreeAndClearBitTest()
    {
        Span<ulong> map = stackalloc ulong[4];

        // Find set bit 0
        var b0 = map.FindFreeBitConcurrent();
        Assert.That(b0, Is.EqualTo(0));
        Assert.That(map.IsBitSet(b0), Is.True);
        
        // Find set bit 1
        var b1 = map.FindFreeBitConcurrent();
        Assert.That(b1, Is.EqualTo(1));
        Assert.That(map.IsBitSet(b1), Is.True);
        
        // Clear bit 0 and check
        map.ClearBitConcurrent(b0);
        Assert.That(map.IsBitSet(b0), Is.False);

        // Manually set bit 0
        map.SetBitConcurrent(0);
        Assert.That(map.IsBitSet(b0), Is.True);

    }
    
    [Test]
    public void FindFreeAndClearBitsTest()
    {
        Span<ulong> map = stackalloc ulong[4];

        // Find set bits 0, 1
        var b0 = map.FindFreeBitsConcurrent(2);
        Assert.That(b0, Is.EqualTo(0));
        Assert.That(map.IsBitSet(b0), Is.True);
        Assert.That(map.IsBitSet(b0+1), Is.True);
        
        // Find set bits 2, 3
        var b2 = map.FindFreeBitsConcurrent(2);
        Assert.That(b2, Is.EqualTo(2));
        Assert.That(map.IsBitSet(b2), Is.True);
        Assert.That(map.IsBitSet(b2 + 1), Is.True);
        
        // Clear bits 0, 1 and check
        map.ClearBitConcurrent(b0);
        map.ClearBitConcurrent(b0+1);
        Assert.That(map.IsBitSet(b0), Is.False);
        Assert.That(map.IsBitSet(b0+1), Is.False);

        // Manually set bits 0, 1
        map.SetBitsConcurrent(0, 2);
        Assert.That(map.IsBitSet(b0), Is.True);
        Assert.That(map.IsBitSet(b0+1), Is.True);

        // Manually clear bits 0, 1
        map.ClearBitsConcurrent(0, 2);
        Assert.That(map.IsBitSet(b0), Is.False);
        Assert.That(map.IsBitSet(b0+1), Is.False);

        var max = map.FindMaxBitSet();
        Assert.That(max, Is.EqualTo(3));
    }

    [Test]
    public void EnumerateBitSetTest()
    {
        Span<ulong> map = stackalloc ulong[4];
        map[0] = 0x80_40_00_00_14_00_00_80U;
        map[1] = 0x40_80_00_00_28_00_00_40U;
        map[2] = 0x0FU;

        var bitset = new[] { 7, 26, 28, 54, 63, 70, 91, 93, 119, 126, 128, 129, 130, 131 };
        var index = 0;

        var i = -1;
        while (index < bitset.Length)
        {
            var res = map.FindSetBitConcurrent(ref i);

            Assert.That(res, Is.True, $"Is: {i} should be {bitset[index]}");
            Assert.That(i, Is.EqualTo(bitset[index]), $"Is: {i} should be {bitset[index]}");

            ++index;
        }

        {
            var res = map.FindSetBitConcurrent(ref i);
            Assert.That(res, Is.False);
            Assert.That(i, Is.EqualTo(-1));
        }
    }
}
