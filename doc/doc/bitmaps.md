---
uid: bitmaps-overview
---

# Bitmaps overview

Bitmaps are...maps of bits...you allocate a bitmap with a given capacity of _n_ bits, then you can allocate/free bits (when the bit is `0`: it's free, when it's `1`: it's occupied).

This allows the implementation of a occupancy map, for instance, the simpler example is the [PageAllocator](<xref:Tomate.PageAllocator>) type.

Bitmap are designed for concurrent accesses, their implementation may vary in term of what matters the most, but most of the time it is for the sake of a good balance between concurrency and allocation efficiency. 

You could argue they are not that friendly toward [false sharing](https://en.wikipedia.org/wiki/False_sharing) and you would be right, but synchronizing through some kind of [Interlocked](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked) operation would generate contention on the whole bitmap content and wouldn't scale as well the bigger the map is.

In any case, this type of resource is persistent and inter-process friendly, so it plays very well with the Memory Mapped Files.

 Currently, 🍅 implements the [ConcurrentBitmapL3All](<xref:Tomate.ConcurrentBitmapL3All>) and the [ConcurrentBitmapL4](<xref:Tomate.ConcurrentBitmapL4>) types.