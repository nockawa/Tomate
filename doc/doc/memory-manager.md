# Memory Manager Overview

There is only one way to avoid GC in .net when you're manipulating data with a indeterminate lifecycle: a custom memory Manager.

That's right, you trade ease of use against performance, which is not unusual.
The responsibility of the memory lifecycle shift on your side, some features and helpers ease things for you, but still, it's your job now.

## Memory Manager and friends
Tomate declare the [`IMemoryManager`](<xref:Tomate.IMemoryManager>) interface which has two implementations so far

1. [`DefaultMemoryManager`](<xref:Tomate.DefaultMemoryManager>): which is the default memory manager for in-process memory allocation.
2. [`MemoryManagerOverMMF`](<xref:Tomate.MemoryManagerOverMMF>): which is the memory manager that maps a memory-mapped-file (MMF) for interprocess communication/processing and/or persistent data storage.

The purpose of a Memory Manager is to allocate, resize and free memory blocks while being thread-safe.

### Memory Block

The memory manager is used to allocated linear segments of memory, each one has a fixed address and is represented by the [`MemoryBlock`](<xref:Tomate.MemoryBlock>) type.

From an instance of [`MemoryBlock`](<xref:Tomate.MemoryBlock>) you can:
1. Extend/release the lifecycle through reference counting.
2. Resize the block, which will allocate a new one, copying the content of the existing and release it. So your instance will point to a new address.
3. Release/Dispose the memory block.
4. Accessing the underlying [`MemorySegment`](<xref:Tomate.MemorySegment>).

### Memory Segment

[`MemorySegment`](<xref:Tomate.MemorySegment>) a struct that simply describes a linear segment of memory data, it's made of an address and a length.

A MemoryBlock can be seen as a MemorySegment, but the reverse is not necessarily true. You can for instance split a segment in two, the second one won't be a valid MemoryBlock.

You can convert a MemorySegment into a [`Span<T>`](<xref:System.Span`1>) and use the .net APIs relying on it for safe memory access.

Note that while doing this you should ensure the __lifetime of the underlying memory block is adequate.__

## Default Memory Manager

The implementation of [`DefaultMemoryManager`](<xref:Tomate.DefaultMemoryManager>) was deliberately kept aside from being too complex, but still with the goal of achieving overall decent performances.

You can have multiple instance of this type, but you are encourage to use the default one, accessible from the static property of its type.

## Memory Manager over a Memory Mapped File

A memory mapped file (MMF for short) serves two purpose:
1. Acts as a medium for interprocess communication, data exchange.
2. Stores persistent data that can be consumed, processed directly through .net instances.

The [`MemoryManagerOverMMF`](<xref:Tomate.MemoryManagerOverMMF>) type allows to interact with a MMF through a Memory Manager.
