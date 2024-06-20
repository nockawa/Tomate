---
uid: collections-overview
---

# Collections overview

## Unmanaged* collections overview
Each collection type starting with `Unmanaged` applies to this section, the most simple and notable one being [`UnmanagedList<T>`](<xref:Tomate.UnmanagedList`1>).

### Implementation overview
Unmanaged collections are `struct` based types that rely on a [`IMemoryManager`](<xref:Tomate.IMemoryManager>) to store the collection's data.

Most of the time, the struct in itself stores:
- A [`MemoryBlock`](<xref:Tomate.MemoryBlock>) that points to the allocated memory segment which contains a header (e.g.: storing capacity, count) and the items stored in the collection.
- Other fields present to manage the collection more efficiently (cached pointers, see below).

Data stored in the MemoryBlock must be process-independent, because Unmanaged Collection are designed to be used along with a [`MemoryManagerOverMMF`](<xref:Tomate.MemoryManagerOverMMF>) which allows the collection to be stored in a Memory Mapped File and be shared between processes. So it means, no addresses, no pointers, no references, only offsets.

The Unmanaged collection wraps the MemoryBlock and provides the API to interact with the data, it also contains process-dependent fields like pointer to the header, pointer to the items, etc. to boost performances of the operations. Each API call check these accelerations fields are up-to-date, and refresh them if needed.

### Usage overview
Working with value types imposes specific constraints, the purpose of 🍅 is to make it as easy as possible for the user.
Two cases are to be considered:

#### Short-lived, single user instances

Using an instance bound by a defined scope:

```csharp

{
    // Create the list instance
    using var myList = new UnmanagedList<int>();
    
    // Add 10 items with values from 0 to 9
    for (int i=0; i<10; i++)
        myList.Add(i);
    
    PrintContent(ref myList);
}                           // myList is disposed here, the MemoryBlock allocated is released  

// Here the list could be passed by copy instead of by reference, it would work in this case because the list is not
//  mutated by this method and we know the caller doesn't too. But it's a dangerous practice and should be avoided.
void PrintContent(ref UnmanagedList<int> list)
{
    // this will print the content of the list
    foreach (var val in list)
        Console.WriteLine(val);
}
```

#### Bound to a custom type
Using an instance as a field of a custom type:

```csharp
public class MyType : IDisposable
{
    public MyType()
    {
        _myList = new UnmanagedList<int>();
    }

    // Provide a ref access to the list, which allow the caller to mutate its content safely
    public ref UnmanagedList<int> List => ref _myList;
    
    public void Add(int val)
    {
        _myList.Add(val);
    }

    public void PrintContent()
    {
        foreach (var val in _myList)
            Console.WriteLine(val);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        _myList.Dispose();
        _myList = default;
        GC.SuppressFinalize(this);
    }

    public bool IsDisposed => _myList.IsDefault;

    // Not mandatory, but if the user ever forget to call Dispose, the memory of the list will be released during 
    //  GC collection.
    // The type could also not implement IDisposable and we would only rely on the destructor to free the memory.
    ~MyType()
    {
        Dispose();
    }

    private UnmanagedList<int> _myList;
}
```

> [!WARNING]
> Note that users have access to the List through a `ref` property, which allows them to use the very same instance as the class itself.
> 
> Again, using a copy of the list instance would work fine in very specific cases, you still "share" the content with the initial instance, but if one or the other instance mutates the list and that triggers a reallocation of the MemoryBlock, the other instance will be left with a corrupted instance pointing to a memory block that no longer exists and without the possibility to know it no longer does.

#### Sharing instances through a store
If you need to share an instance through multiple users for a controlled lifetime, you can use a [`UnmanagedDataStore`](<xref:Tomate.UnmanagedDataStore>).

As we established the safest way to manipulate the unmanaged collections is through a `ref` access, there are a set of limitations arising, the main one being it's not possible to declare a `ref` field in a type (unless the type is a ref type itself, but the limitations are just escalated). which make it very hard to _point_ to a given instance from multiple places.

The store offers a place where the instances are stored for a lifetime you control, each instance is identified and accessed by a [`handle`](<xref:Tomate.UnmanagedDataStore.Handle>) which is safe to store anywhere (even in a MemoryMappedFile).
This handle also is safe to use because it knows if the instance it points to is still valid or no longer. Using a handle is lifetime safe, type safe and its size is not bigger than a 64-bit pointer.

For convenience, each implementation of [`IMemoryManager`](<xref:Tomate.IMemoryManager>) has a corresponding [`store`](<xref:Tomate.IMemoryManager.Store>), so you can use it directly or create your own, if needed.

You can also, for instance, create list of lists, for example:

```csharp
// Create the main list, not stored in the store for the sake of the example, but should be
UnmanagedList<UnmanagedDataStore.Handle<UnmanagedList<int>>> globalList = new();

{
    // Create a list that will be directly stored in the store
    ref var childList = ref UnmanagedList<int>.CreateInStore(null, 8, out var handle);
    childList.Add(10);
    
    // Add the childList to the globalList
    globalList.Add(handle);
}
// ... later on
{
    // Retrieve the childList from the globalList through the store
    ref var childList = ref UnmanagedList<int>.GetFromStore(null, globalList[0]);
    Assert.That(childList[0], Is.EqualTo(10));
}
```

### __TL;DR__

Three levels:
1. The plain data, stored in a MemoryBlock, that is process-independent and can be stored inside a MemoryMappedFile. The user is not supposed to interact with it directly.
2. The Unmanaged Collection, a struct that wraps the MemoryBlock and provides the API to interact with the data. The user can interact with it in multiple ways, depending on the use case. You should always work with it through a `ref` access, pass it/return it as a `ref`.
3. As working with `ref` of struct implies restrictions, you can rely on the [`UnmanagedDataStore`](<xref:Tomate.UnmanagedDataStore>) to safely store, access and share instances. 

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

Not written yet...