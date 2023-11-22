---
uid: Tomate.IMemoryManager
---

## About

`IMemoryManager` is the interface used to implement a custom Memory Manager.
The manager duty is to allocated, resize, free block of memory.
🍅 implements many memory manager, you can find more about it [here](<xref:memory-management-overview>).

Collections and other types that rely on memory allocation will ask for a given instance implementing this interface.

### `unmanaged struct` limitation

When we do GC-free programming, we want to stay away from class based instances, but we still have to deal with some of them sometimes.

In this case, types implementing `IMemoryManager` are meant to be class based, but at some point, if you want to store an instance of a given Memory Manager in say, your [`UnmanagedList<T>`](<xref:Tomate.UnmanagedList`1>) instance, you can't declare such field if you want your struct to be considered as [unmanaged](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).

For such reason we have methods in this interface to [Register](<xref:Tomate.IMemoryManager.RegisterMemoryManager(Tomate.IMemoryManager)>) and instance, which return an ID that can be stored anywhere. Then you can access a given Memory Manager from its ID using [GetMemoryManager()](<xref:Tomate.IMemoryManager.GetMemoryManager(System.Int32)>).

## Definition