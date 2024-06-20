using NUnit.Framework;

namespace Tomate.Tests;

public class MemoryManagerOverMMFTests
{
    private const long DefaultMMFSize = 1 * 1024 * 1024 * 1024;
    private const int DefaultPageSize = 4 * 1024 * 1024;

    public struct TestA
    {
        public float A;
        public int B;
        public int C;
        public float D;
    }

    [Test]
    public void Test()
    {
        var filePathName = Path.GetRandomFileName();
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePathName);
            {
                IProcessProvider.Singleton = new MockProcessProvider();
                using var mmf = MemoryManagerOverMMF.Create
                (
                    new MemoryManagerOverMMF.CreateSettings(filePathName, fileName, DefaultMMFSize, DefaultPageSize, false)
                );
                Assert.That(mmf, Is.Not.Null);

                // Inside a scope to ensure they are disposed before the MMF
                {
                    using var seg1 = mmf.Allocate(32);
                    Assert.That(seg1.RefCounter, Is.EqualTo(1));
                    Assert.That(seg1.IsDefault, Is.False);
                    Assert.That(seg1.MemorySegment.Length, Is.EqualTo(32));
            
                    using var seg2 = mmf.Allocate(16);
                    Assert.That(seg2.RefCounter, Is.EqualTo(1));
                    Assert.That(seg2.IsDefault, Is.False);
                    Assert.That(seg2.MemorySegment.Length, Is.EqualTo(16));

                    using var seg3 = mmf.Allocate(24);
                    Assert.That(seg3.RefCounter, Is.EqualTo(1));
                    Assert.That(seg3.IsDefault, Is.False);
                    Assert.That(seg3.MemorySegment.Length, Is.EqualTo(24));
                }

                {
                    mmf.Defragment();
                    var size = 250_000;
                    using var mb1 = mmf.Allocate<TestA>(size);
                    var seg = mb1.MemorySegment;

                    for (int i = 0; i < size; i++)
                    {
                        ref var t = ref seg[i];
                        t.A = i;
                        t.B = i + 10;
                        t.C = i * 2;
                        t.D = i * 2 + 20;
                    }

                }

            }
        }
        finally
        {
            MemoryManagerOverMMF.Delete(filePathName);
        }
    }

    [Test]
    public void ResourceLocatorTest()
    {
        const int listCapacity = 256;
        
        var filePathName = Path.GetRandomFileName();
        var fileName = Path.GetFileNameWithoutExtension(filePathName);
        try
        {
            {
                using var mmf = MemoryManagerOverMMF.Create
                (
                    new MemoryManagerOverMMF.CreateSettings(filePathName, fileName, DefaultMMFSize, DefaultPageSize, false)
                );
                Assert.That(mmf, Is.Not.Null);

                // A first list as resource
                var ul1 = new UnmanagedList<int>(mmf, listCapacity);

                var mm = ul1.MemoryManager;
                for (var i = 0; i < listCapacity; i++)
                {
                    ul1.Add(100 + i);
                }
                
                var res = mmf.TryAddResource("ResourceA", ul1);
                Assert.That(res, Is.True);

                // A second one
                var ul2 = new UnmanagedList<int>(mmf, listCapacity);
                for (var i = 0; i < listCapacity; i++)
                {
                    ul2.Add(200 + i);
                }
            
                res = mmf.TryAddResource("ResourceB", ul2);
                Assert.That(res, Is.True);
            }

            {
                using var mmf = MemoryManagerOverMMF.Open(filePathName, fileName);
                Assert.That(mmf, Is.Not.Null);
                
                ref var ul1 = ref mmf.TryGetResource<UnmanagedList<int>>("ResourceA", out var res);
                Assert.That(res, Is.True);
                
                for (var i = 0; i < listCapacity; i++)
                {
                    Assert.That(ul1[i], Is.EqualTo(100 + i));
                }
                
                ref var ul2 = ref mmf.TryGetResource<UnmanagedList<int>>("ResourceB", out res);
                Assert.That(res, Is.True);
                
                for (var i = 0; i < listCapacity; i++)
                {
                    Assert.That(ul2[i], Is.EqualTo(200 + i));
                }
            }
        }
        finally
        {
            MemoryManagerOverMMF.Delete(fileName);
        }
    }
}