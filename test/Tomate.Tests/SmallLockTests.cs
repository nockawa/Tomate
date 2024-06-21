using NUnit.Framework;

namespace Tomate.Tests;

public class SmallLockTests
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        IProcessProvider.Singleton = new MockProcessProvider();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
    }
    
    [Test]
    public void ManyConsecutiveLocksTest()
    {
        const int maxConcurrency = 4;
        const int loopSize = 64;
        const int lockId = 123;
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        for (int i = 0; i < loopSize; i++)
        {
            l.TryEnter(out var lockTaken, lockId);
            Assert.That(lockTaken, Is.True, $"[{i}] Lock should be taken, but was not");
            Assert.That(l.IsEntered, Is.True, $"[{i}] Lock should be entered, but was not");
            
            l.Exit(lockId);
            Assert.That(l.IsEntered, Is.False, $"[{i}] Lock should be not entered, but was is");
        }
    }

    [Test]
    public void NestedEnterExitTest()
    {
        const int maxConcurrency = 4;
        const int nestedLevels = 6;
        const int lockId = 123;
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        for (var i = 0; i < nestedLevels; i++)
        {
            l.TryEnter(out var lockTaken, lockId);
            Assert.That(lockTaken, Is.True, $"[{i}] Lock should be taken, but was not");
        }
        
        for (int i = 0; i < nestedLevels - 1; i++)
        {
            l.Exit(lockId);
            Assert.That(l.IsEntered, Is.True, $"[{i}] Lock should be entered, but was not");
        }
        l.Exit(lockId);
        Assert.That(l.IsEntered, Is.False, $"Lock is still held after the last exit of a nested scenario");
    }

    [Test]
    public void ExitWithWrongIdTest()
    {
        const int maxConcurrency = 4;
        const int lockId = 123;
        const int badLockId = 321;
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        l.TryEnter(out _, lockId);
        Assert.Throws<ArgumentException>(() => l.Exit(badLockId));
    }

    [Test]
    public void ConcurrencyExceededTest()
    {
        const int maxConcurrency = 4;
        const int lockId = 10;

        Thread.CurrentThread.Name = "*** Unit Test Running Thread ***";
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        var exitThread = false;
        var exitThreadCounter = 0;
        for (var i = 0; i < maxConcurrency; i++)
        {
            var curLockId = lockId + i;
            //var i1 = i;
            var t = new Thread(() =>
            {
                Thread.CurrentThread.Name = $"*** Unit Test Worker #{curLockId} Thread ***";
                l.TryEnter(out _, curLockId);

                var sw = new SpinWait();
                // ReSharper disable once AccessToModifiedClosure
                // ReSharper disable once LoopVariableIsNeverChangedInsideLoop
                while (exitThread == false)
                {
                    sw.SpinOnce();
                }
                l.Exit(curLockId);
                Interlocked.Increment(ref exitThreadCounter);
            });
            t.Name = $"Lock Thread {curLockId}";
            t.Start();
        }
        
        var sw = new SpinWait();
        while (l.ConcurrencyCounter < maxConcurrency)
        {
            sw.SpinOnce();
        }
        
        // We are expecting a throw on this last TryEnter because maxConcurrency is reached
        Assert.Throws<SmallLockConcurrencyExceededException>(() => l.TryEnter(out _, lockId + maxConcurrency));
        
        exitThread = true;
        while (exitThreadCounter < maxConcurrency)
        {
            sw.SpinOnce();
        }
    }

    [Test]
    public void ResumeOnProcessCrashTest()
    {
        const int maxConcurrency = 4;
        const int secondProcessId = 400;
        const int firstProcessLockId = 200;
        const int secondProcessLockId = 300;
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        var firstProcessLockHold = false;
        var secondProcessLockHold = false;
        var ipp = (MockProcessProvider)IProcessProvider.Singleton;
        var t = new Thread(() =>
        {
            ipp.CurrentProcessId = secondProcessId;
            
            l.TryEnter(out var lockTaken, secondProcessLockId);
            Assert.That(lockTaken, Is.True);
            Assert.That(l.IsEntered, Is.True);
            
            secondProcessLockHold = true;

            var sw = new SpinWait();
            // ReSharper disable once AccessToModifiedClosure
            // ReSharper disable once LoopVariableIsNeverChangedInsideLoop
            while (firstProcessLockHold == false)
            {
                sw.SpinOnce();
            }
        });

        try
        {
            t.Name = "Simulated Second Process thread";
            t.Start();

            Assert.That(l.IsEntered, Is.False);
            
            // Wait for the second thread to hold the lock
            var sw = new SpinWait();
            while (Volatile.Read(ref secondProcessLockHold) == false)
            {
                sw.SpinOnce();
            }

            Assert.That(l.IsEntered, Is.True);
            
            // We shouldn't be able to enter the lock, it's still hold by the other thread
            l.TryEnter(out var lockTaken, firstProcessLockId, TimeSpan.FromMilliseconds(100));
            Assert.That(lockTaken, Is.False);

            // Unregister the process matching the second thread
            // This will declare the process as dead, then a subsequent TryEnter should be able to get control
            ipp.UnregisterProcess(secondProcessId);

            // Try again, this should work
            l.TryEnter(out lockTaken, firstProcessLockId);
            Assert.That(lockTaken, Is.True);
        }
        finally
        {
            firstProcessLockHold = true;
        }
    }

    [Test]
    [TestCase(0, 3)]
    [TestCase(1, 2)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(4, 3)]
    public void TimeoutTriggeredTest(int lockCountBefore, int indexLockWithTimeOut)
    {
        const int maxConcurrency = 4;
        const int lockId = 10;
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        var l = SmallLock.Create((MemorySegment)segData);

        for (int i = 0; i < lockCountBefore; i++)
        {
            l.TryEnter(out _);
            l.Exit();
        }
        
        var exitThreadCounter = 0;
        for (var i = 0; i < maxConcurrency; i++)
        {
            var lockIndex = i;
            var curLockId = lockId + lockIndex;
            var t = new Thread(() =>
            {
                if (lockIndex == indexLockWithTimeOut)
                {
                    l.TryEnter(out var lockTaken, curLockId, TimeSpan.FromMicroseconds(50));
                    Assert.That(lockTaken, Is.False);
                }
                else
                {
                    l.TryEnter(out _, curLockId);

                    Thread.Sleep(100);
                
                    l.Exit(curLockId);
                }
                Interlocked.Increment(ref exitThreadCounter);
            });
            t.Name = $"Lock Thread {curLockId}";
            t.Start();
        }
        
        var sw = new SpinWait();
        while (exitThreadCounter < maxConcurrency)
        {
            sw.SpinOnce();
        }
        
        l.TryEnter(out var taken, 400, TimeSpan.FromMilliseconds(50));
        Assert.That(taken, Is.True);
        Assert.That(l.ConcurrencyCounter, Is.EqualTo(1));
        l.Exit(400);
    }

    // A very slow, read all, inc all, store all
    // If the given array is not under lock, well it's most likely to create inconsistent values...
    private unsafe void SlowAggCounters(int[] counters)
    {
        var counterSize = counters.Length;
        Span<int> temp = stackalloc int[counterSize];
        for (int i = 0; i < counterSize; i++)
        {
            temp[i] = counters[i];
        }

        for (int i = 0; i < counterSize; i++)
        {
            ++temp[i];
        }

        for (int i = 0; i < counterSize; i++)
        {
            counters[i] = temp[i];
        }
    }

    [Test]
    public unsafe void ConcurrencyStressTest()
    {
        var maxConcurrency = (ushort)(Environment.ProcessorCount * 1);      // Should be ideally 4, but it's too damn slow at execution
        const int lockId = 10;
        const int timeOutInSecond = 30;
        const int counterSize = 1024;

        var counters = new int[counterSize];
        
        var segSize = SmallLock.ComputeSegmentSize(maxConcurrency);
        Span<byte> segData = stackalloc byte[segSize];
        
        // Create the lock
        var l = SmallLock.Create((MemorySegment)segData);
        Assert.That(l.ConcurrencyCapacity, Is.EqualTo(maxConcurrency));

        // Main loop, create a number of threads equal to maxConcurrency
        // For each we wait a small indeterminate time, then lock, update the counters array, and exit the lock
        // This will create a big contention on the lock, the only way for counters to stay consistent is if the lock is working
        var threadEnterCount = 0;
        var threadExitCount = 0;
        var rnd = new Random(DateTime.UtcNow.Millisecond);
        
        for (var i = 0; i < maxConcurrency; i++)
        {
            var curLockId = lockId + i;
            
            // Create the thread 
            var t = new Thread(() =>
            {
                // Wait before entering
                Thread.Sleep(rnd.Next(0, 16));
                
                // Enter
                Interlocked.Increment(ref threadEnterCount);
                l.TryEnter(out _, curLockId);

                // These counters are updated inside the lock
                SlowAggCounters(counters);
                
                // Wait before exiting
                Thread.Sleep(rnd.Next(0, 16));
                
                l.Exit(curLockId);
                Interlocked.Increment(ref threadExitCount);
            });
            t.Name = $"Lock Thread {curLockId}";
            
            // Start the thread
            t.Start();
        }

        // Let's wait all threads have been exiting...or timeout reached
        var sw = new SpinWait();
        var maxWait = DateTime.UtcNow + TimeSpan.FromSeconds(timeOutInSecond);
        while (threadExitCount < maxConcurrency)
        {
            if (DateTime.UtcNow >= maxWait)
            {
                Assert.Inconclusive($"Timeout of {timeOutInSecond} seconds reached, the test is most likely failing.\r\nThread enter count {threadEnterCount}, thread exit count {threadExitCount}");
                return;
            }
            sw.SpinOnce();
        }
        
        // Lock should be not entered
        Assert.That(l.IsEntered, Is.False);

        // Now check that all counters have the expected value
        for (int i = 0; i < maxConcurrency; i++)
        {
            Assert.That(counters[i], Is.EqualTo(maxConcurrency));
        }
    }
}