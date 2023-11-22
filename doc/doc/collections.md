---
uid: collections-overview
---

# Collections overview

## Unmanaged* collections overview
Each collection type starting with `Unmanaged` applies to this section, the most simple and notable one being [`UnmanagedList<T>`](<xref:Tomate.UnmanagedList`1>).

### Implementation overview
Unmanaged collections are `struct` based types that rely on a [`IMemoryManager`](<xref:Tomate.IMemoryManager>) to store the collection's data.

Most of the time, the struct in itself stores only a [`MemoryBlock`](<xref:Tomate.MemoryBlock>) that points to the allocated memory segment which contains a header (e.g.: storing capacity, count) and the items stored in the collection.

> [!WARNING]
> You _could_ pass an instance (of say an `UnmanagedList`) by copy, it would be fine as long as the List's content is not resized, meaning allocating a new `MemoryBlock`, which would change the content of the struct instance.
> 
> But such behavior is dangerous and should be avoided, you should __always__ pass such collection by ref (using the `ref` access modifier).

### Lifecycle

These collection have a [`IsDefault`](<xref:Tomate.UnmanagedList`1.IsDefault>) and [`IsDisposed`](<xref:Tomate.UnmanagedList`1.IsDisposed>) properties for you to know if the instance is valid (`IsDefault == false`) or no longer usable because it's disposed (`IsDisposed == true`).

You can also _share_ a given instance through multiple users, each with their own lifetime by calling [`AddRef()`](<xref:Tomate.UnmanagedList`1.AddRef*>) to extend and a corresponding [`Dispose()`](<xref:Tomate.UnmanagedList`1.Dispose*>) to release usage for this particular user.

### Enumeration of items
While these collections don't implement the [`IEnumerable<T>`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerable-1) interface, enumeration of their items is still possible as you would expect because they implement the `GetEnumerator()` method (more details [here](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/iteration-statements?redirectedfrom=MSDN#the-foreach-statement).

The enumerator also expose the item as a `ref T` which allows you to iterate on the actual value and not a copy of it.

```csharp
// Create the list instance
using var myList = new UnmanagedList<int>();

// Add 10 items with values from 0 to 9
for (int i=0; i<10; i++)
    myList.Add(i);

// this will add 100 to each item in the list as val is passed by ref
foreach (ref int val in myList)
    val += 100;         
```

## Mapped* collections overview