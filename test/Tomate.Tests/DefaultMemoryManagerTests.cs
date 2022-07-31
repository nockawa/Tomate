using NUnit.Framework;

namespace Tomate.Tests;

public class DefaultMemoryManagerTests
{
    [Test]
    public void SimpleTest()
    {
        var mm = new DefaultMemoryManager();

        var s0 = mm.Allocate(22);
        var s1 = mm.Allocate(1024);
    }
}