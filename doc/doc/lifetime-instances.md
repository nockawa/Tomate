---
uid: lifetime-instances-overview
---

# Lifetime of unmanaged struct-based instances
.net features two kinds of types: classes and structs.

- Instances of classes (aka reference types) have their lifetime tracked by the references to each of them, the garbage collector takes care of releasing the memory of instances that are no longer reachable. It couldn't be easier for the user and it's as safe as it can be.
- Instances of struct (aka value types) are...different. Initially in .net a struct was only passed through method calls and/or instances' field by __copy only__. Things got more complex when microsoft introduced the concept of [ref struct](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct).

## Unmanaged struct and GC-free "long-term" storage
Struct based types can be split into two kinds:
- A struct that declares some of its fields using the class type.
- A struct that answers to a set of criteria which makes it an [unmanaged struct](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).

Unmanaged structs are very interesting because they have absolutely __no tie__ with the GC, and this is what 🍅 is about.

Another _trick_ is to rely on the [Unsafe.AsRef<T>(void*)](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.unsafe.asref#system-runtime-compilerservices-unsafe-asref-1(system-void*)) API to conveniently represent an area of memory of __any kind__ as a ref struct instance.

So, it is possible in .net to [allocate](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocateuninitializedarray) a big memory block (of managed memory), to [pin](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.unsafeaddrofpinnedarrayelement#system-runtime-interopservices-marshal-unsafeaddrofpinnedarrayelement(system-array-system-int32)) this block (to make sure its address won't change, which is equivalent to a _native_ memory block) and use it the way we would in C/C++.

Example:
```csharp
var array = GC.AllocateUninitializedArray<byte>(64 * 1024 * 1024, true);
var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(array, 0).ToPointer();
```
From this point, we can store any data we want in this memory block and expose its content as `ref struct` instances of unmanaged type.

But anything has a cost, pros/cons, in our case the lifetime of _things_ is not as safe as classic .net would be, we must make sure everything is taken care of correctly to ensure we won't access a `ref struct` instance of a memory block we just freed!

One of the purpose of 🍅 is to ensure the lifetime is taken care of for you, with the minimal impact on the way you code. It will be more complex than _classic_ .net programming, but performances will be better too.

## Reference counting to the rescue
It's a popular, simple and proven mechanic that allows multiple _virtual_ users to share the usage of a given instance.

In 🍅, it is the [IRefCounted](<xref:Tomate.IRefCounted>) interface that takes care of this. Any type implementing this interface has its lifetime controlled by a reference counter.

Each logical user calls [AddRef()](<xref:Tomate.IRefCounted.AddRef>) to extend the lifetime of the instance as long as it need.

> [!NOTE]
> `IRefCounted` derives from [IDisposable](<xref:System.IDisposable>), calling `Dispose()` acts as decrementing the reference counter, if it is reaching `0`, then the instance is disposed, otherwise the instance is still alive, because still being used by others.

### Typical usage scenario
Each instance is created by a logical user, we will call this user the __owner__. The owner has its own agenda concerning the lifetime of the instance, when it's done with it, `Dispose()` is called.

In the meantime, if another logical user comes into play, for instance:
1. User `A` creates instance `Z`.
2. User `A` call a method of user `B`, passing `Z` as parameter.
3. `B` needs to store `Z` and keep it for a indefinite amount of time.
4. `A` no longer needs `Z`.
5. `B` still is using `Z`.
6. `B` no longer needs `Z`.

It is crucial to understand that:
- In step 3, `B` needs to call `Z.AddRef()` to extend its lifetime, setting the reference counter to `2`.
- In step 4, `A` will call `Z.Dispose()`, but `Z` will still exists ([Z.IsDisposed](<xref:Tomate.IRefCounted.IsDisposed>) is `false`) because it had a reference counter of `2` and `Dispose()` decremented down to `1`.
- In step 6, `B` needs to call `Z.Dispose()`, which will set the reference counter to `0`, which will trigger a disposing of the object ([Z.IsDisposed](<xref:Tomate.IRefCounted.IsDisposed>) is `true`).

## Recap of the lifetime for struct, collection, `IRefCounted` based types

### struct
A struct instance's lifetime is bound by the component that __stores it__. So in itself there is no point to make a struct type implementing `IRefCounted`.

__Unless__ your struct is more a handle than storing data, which is the case for the `Unmanaged*` collections, the struct is simply a handle, concretely it stores a [`MemoryBlock`](<xref:Tomate.MemoryBlock>) that contains the actual data, and other fields specific to the implementation of the type itself.  
The `MemoryBlock` stores all the data for the collection (a header + the actual items).  
The `IRefCounted` interface of `UnmanagedList<T>` defers to the underlying `MemoryBlock` which support reference counting.

__TL;DR__   
- [`IRefCounted`](<xref:Tomate.IRefCounted>) allows to __share__ an instance.
- [`MemoryBlock`](<xref:Tomate.MemoryBlock>) implemented `IRefCounted` and can be shared.
- A struct's instance can't be shared, unless the instance is more a handle/facade and the data in itself is stored to a location that can be shared.
- `Unmanaged*` collections can be shared for the reason cited above (see more [here](<xref:collections-overview#unmanaged-collections-overview>))

## Beware of undesired copies!
One could say and argue there is a design flaw in C# because it is very easy to copy an instance of a struct instead of using a reference of it.

C/C++ languages have the notion of pointer (then address), which is semantically different from the instance itself. It's either your pass the instance (and it's a copy) or you pass a pointer of the instance (and you copy the pointer itself, not the instance).

In C#, it's _easy_ to do things the wrong way because you can have a method that returns a ref to struct and yet, if you don't pay attention and do the "usual stuff", you will end up copying this returned instance.

Example:
```csharp
public ref UnmanagedList<int> GetList() { ref return myList; }

...

// The wrong way   
var theList = GetList();            // here `theList`is a copy of `myList`

// The right way
ref var theList2 = ref GetList();   // here `theList2` is the same instance as `myList`
```

> [!WARNING]
> Both invocations of `GetList()` are compiling, and that's where things are dangerous and could be considered a design flaw.
> 
> One could argue that a method doing a ref return is, by designed, expected the caller to get a reference of the returned instance and not copying it.
> 
> People being working with class based instance >90% of the time and being not familiar with `ref return` and `ref var` could easily get it wrong.

> [!NOTE]
> Back to our example above, if you do the first call, ending up copying the `UnmanagedList`, in appearances and concretely, you can use the list just fine... as long as you are not calling a method that will result in resizing the list's content (or the original owner of the list also does it).
> 
> But it's clearly risky and should be avoided at all cost, but the fact that it won't lead to an unexpected behavior right way makes it very treacherous.

## Beware of ref access
Consider this example:
```csharp
using var ul = new UnmanagedList<int>();

ul.Add(0);
ul.Add(1);
ul.Add(2);

ref var i1 = ref ul[1];             // Reference to the 2dn item of the list
Assert.That(i1, Is.EqualTo(1));     // So far so good

ul.RemoveAt(0);                     // Now we remove the first element, all subsequent shift one slot to the left.
Assert.That(i1, Is.EqualTo(1));     // It fails because i1 is pointing to the second location which now is 2. 
                                    // 0, [1], 2 before RemoveAt(). 0, [2] after.
```
The subscript (i.e. `[]`) operator is really convenient in its `ref return` form, but you have to be aware of what it really represents and its true scope of usage.  
In the example above, `i1` represents the 2nd location of the list, it __doesn't__ represent the item stored at this location.  
Doing `var it1 = ul[1]` would have been getting a copy of the item.

> [!WARNING]
> A `ref var` is an __address__ always, treat it as a such.