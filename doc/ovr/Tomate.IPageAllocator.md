---
uid: Tomate.IPageAllocator
---

## About

The `IPageAllocator` interface allows one to implement a very simple allocator which allocates fixed-size pages. 

It is possible to allocate consecutive pages, the valid range is [1-64].

🍅 implements a simple page allocator with the [PageAllocator](<xref:Tomate.PageAllocator>) type.

## Definition