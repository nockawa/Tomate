# 🍅 Tomate 🍅

Low level library for concurrent, high performance, low GC impact data storing & manipulation.

Also allowing real-time processing of persistent data through a set of collections and primitives that operate over a Memory Mapped File.

## What it is about?
.net standard libraries are great, especially the collection ones, they allow one to write code very quickly/easily.

But if you are looking to squeeze performances or working on very low latency code, you can't afford to create thousand of GC objects anymore. 

Suddenly all the collection types you know and love as well as working with class types all the time is no longer an option.

This library introduces a new way for the user to deal with memory and includes collections and other utility types that are compliant with it. The latest versions of C# and .net allow us to be closer to the metal, this library attempts to get you closer to it.

## What this library targets?

### Performance First
Nowadays, computers are really fast, but it is very hard for many to realize how many things you can do with them. If the programmer doesn't pay attention to performances, the result can quickly get 10x to several 100x times slower.

For many, performances is not an issue, so why paying attention to it if your code runs fast enough? And it's fine.

But if performances is on the top of your list, you know that you have to design you code and develop with a "performance first" mindset.

### Low latency code
If you are doing real-time programming for instance, you have to deal with a lot of things in just few milliseconds. Your territory is in the nanosecond or microsecond execution of your methods. You can't afford to spend you time allocating/releasing memory heaps, you have to pay attention to each data copy.

### Thread-safe, concurrent friendly code
Because our computers have more and more CPU cores, we can't rely on single-threaded code anymore. But multi-threaded code is far from easy and it doesn't get easier when we are targeting low latency code. A mutex lock/unlock is about 100ns, a thread context switch is in the mater of millisecond, so we have to avoid them as much as possible.

This library provides thread-safe types, but not exclusively (because single-threaded will always be faster) to help you and ease things.

## [Usage overview](doc/overview.md)

## So, how?

### A memory manager

Terminology:
 - MemoryArraySegment : internal structure that stores one/many MemorySegment
 - MemorySegment: address/size of a fixed memory segment
 - MemoryBlock: indexed block inside a memory segment

### A set of collection types

Collections are prefixed to give more information about their intended usage:
 - Unmanaged: a single-threaded, generic collection type where T is unmanaged, is not compatible with MemoryMappedFile. It's the default prefix.
 - Mapped: a collection that can be used inside a MemoryMappedFile.
 - Blocked: thread-safe collection but no efficient on its implementation, simplicity and memory footprint chosen over efficiency.
 - Concurrent: thread-safe collection designed and implemented for concurrency intensive use cases.

### Low level synchronization types

These types can be instanced either for runtime purpose or inside a MemoryMappedFile.
 - AccessControl: enable shared read/exclusive write access to a resource where contention is not the issue (there's no waiting list). 
 - ExclusiveAccessControl: smaller version than AccessControl with only exclusive access.
 - SmallLock: An interprocess/thread lock mechanism with a proper waiting list.

### Persistant data manipulation through Memory Mapped File

#### Background
Serializing data over persistant storage is needed when you operate on data structures that rely on pointers. You have to transform the data in order to be saved on disk and you have to code the opposite to load it into memory at loading time.

All of this because of memory pointers...And when we switched from 32bits to 64bits we suddendly had performance hit because of the extra data they take.

.net and C# don't like pointers, so why not created a set of collections and primitives types that are relying on indices instead of pointers to reference data location? It should be at least as fast as pointer, consumming less memory, so maybe a little bit faster. We could also make this data structure persistant and use it directly over Memory Mapped Files. Even better, with the right synchronization primitives types we could use it to exchange data/events between multiples processes sharing the same Memory Mapped File.

This library implements many types for you to achieve this.

#### How

A special type of MemoryManager allow one to manipulate memory through a Memory Mapped File (MMF). A view of a MMF also has a fixed memory address, so it's similar to the concepts of 🍅.

New collection and synchronization types can be used on a MMF the same way you would with the regular ones, allowing the user to easily interact with the file for data-exchange, and also have efficient synchronization between multiple processes.

You can manipulate data structures that are bigger than the available memory and with a lifespan that is bound to the file that store them, not the process that uses them.
You no longer have loading/saving: you operate on the file's content directly, C# types are mapping the collections, structures, content and synchronization object for you.


