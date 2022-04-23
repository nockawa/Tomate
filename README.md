# 🍅 Tomate 🍅

Low level library for concurrent, low GC impact data storing/manipulation.

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

## So, how?

### A memory manager

Terminology:
 - MemoryArraySegment : internal structure that stores one/many MemorySegment
 - MemorySegment: address/size of a fixed memory segment
 - MemoryBlock: indexed block inside a memory segment

### A set of collection types

#### Single-threaded

#### Concurrent

### Low level synchronization types

### Bonus: Memory Mapped File support
A special type of MemoryManager allow one to manipulate memory through a Memory Mapped File (MMF). A view of a MMF also has a fixed memory address, so it's similar to the concepts of 🍅.

So, other types: collections and synchronizations, can be used on a MMF the same way, allowing the user to easily interact with the file for data-exchange, and also have efficient synchronization between multiple processes.