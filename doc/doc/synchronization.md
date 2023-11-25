---
uid: synchronization-overview
---

# Synchronization overview

## Why?
The OS and .NET offer many mechanisms to take care about synchronization between threads and processes.
The traditional `lock` of .net implemented by the [monitor](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor) class is doing a very good job but is general purpose.

🍅 implements custom types with specific features to allows very low latency synchronization between threads and/or processes. 
These types are also designed to take full advantage of multi-core CPU and very short locking span.

## AccessControl
The [AccessControl](<xref:Tomate.AccessControl>) type allows processes/threads to control the usage of a particular resource. Multiple concurrent access are supported through the _shared_ mode, one concurrent access is supported through the _exclusive_ mode.

This type is very small in size (8bytes) and is supported interprocess communication, that is, not only the thread is stored for synchronization but also the process id.

## BurnBabyBurn
The [BurnBabyBurn](<xref:Tomate.BurnBabyBurn>) is a simple type that spins the calling thread for a given time span. 

It allows to attempt an operation for a given time span, until the operation succeed or the time span is reached.

For instance the implementation the [ExclusiveAccessControl.TakeControl()](<xref:Tomate.ExclusiveAccessControl.TakeControl(System.Nullable{System.TimeSpan})>) method.

```csharp
public bool TakeControl(TimeSpan? wait)
{
    var tid = Environment.CurrentManagedThreadId;
    if (Interlocked.CompareExchange(ref _data, tid, 0) == 0)
    {
        return true;
    }

    var bbb = new BurnBabyBurn(wait);
    while (bbb.Wait())
    {
        if (Interlocked.CompareExchange(ref _data, tid, 0) == 0)
        {
            return true;
        }
    }
    return false;
}
```

## ExclusiveAccessControl
The [Tomate.ExclusiveAccessControl](<xref:Tomate.ExclusiveAccessControl>) is a very lightweight access control type (4 bytes), that only works for the calling process (can't be stored in a Memory Mapped File) and supporting only an exclusive access.

This type allows many threads to compete for the exclusive access of a given resource, avoiding concurrent usage.

## SmallLock
The [SmallLock](<xref:Tomate.SmallLock>) implements an interprocess lock mechanism. By storing an instance of this type on a Memory Mapped File you can control the concurrent accesses of a given resource.

At construction, the `SmallLock`'s instance asks for a maximum concurrency level, this number can't be exceeding during usage.
For instance if the level is 4 and you end up having more than 4 process/thread during concurrent operation at a given time, and exception will throw.


