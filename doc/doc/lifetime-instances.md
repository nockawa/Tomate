---
uid: lifetime-instances-overview
---

# Lifetime of unmanaged struct-based instances
.net features two kinds of types: classes and structs.

- Instances of classes have their lifetime tracked by the references to each of them, the garbage collector takes care of release the memory of instances that are no longer reachable.
- Instances of struct are...different. Initially in .net a struct was only pass through method calls and/or instances' field by __copy only__. Things got more complex when microsoft introduced the concept of [ref struct](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct).

## Unmanaged struct and GC-free "long-term" storage
Struct based types can be split into two kinds:
- A struct that declare some of its fields using the class type.
- A struct that answer to a set of criteria which makes it an [unmanaged struct](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).

Unmanaged structs are very interested because they have absolutely __no tie__ with the GC, and this is what 🍅 is about.

Another _trick_ is to rely on the [Unsafe.AsRef<T>(void*)](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.unsafe.asref#system-runtime-compilerservices-unsafe-asref-1(system-void*)) API to conveniently represent an area of __any kind__ of memory as a ref struct instance.

So, it is possible in .net to [allocate](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocateuninitializedarray) a big memory block (of managed memory), to [pin](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.unsafeaddrofpinnedarrayelement#system-runtime-interopservices-marshal-unsafeaddrofpinnedarrayelement(system-array-system-int32)) this block (to make sure it's address won't change, which is equivalent to a _native_ memory block) and use it the way we would in C/C++.

Example:
```csharp
var array = GC.AllocateUninitializedArray<byte>(64 * 1024 * 1024, true);
var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(array, 0).ToPointer();
```
From this point, we can store any data we want in this memory block and expose its content as `ref struct` instances of unmanaged type.

But anything has a cost, pros/cons, in our case the lifetime of _things_ is not as safe as classic .net would be, we must make sure everything is taken care of correctly to ensure we won't access a `ref struct` instance of a memory block we just freed!

One of the purpose of 🍅 is to ensure the lifetime is taken care of for you, with the minimal impact on the way you code. It will be more complex than _classic_ .net programming, but performances will be better too.

## Reference counting to the rescue
It's a popular, simple and proven mechanic that allow multiple _virtual_ users to share the usage of a given object.

In 🍅 it is the [IRefCounted](<xref:Tomate.IRefCounted>) interface that takes care of this. Any type implementing this interface has its lifetime controlled by a reference counter.

> [!NOTE]
> `IRefCounted` derives from [IDisposable](<xref:System.IDisposable>), calling `Dispose()` acts as decrementing the reference counter, it is reached `0`, then the instance is disposed, otherwise the instance is still alive, because used by others.

Each logical user calls [AddRef()](<xref:Tomate.IRefCounted.AddRef>) to extend the lifetime of the instance as long as it need. When the user no longer need this instance, a matching [Dispose()](<xref:System.IDisposable.Dispose>) must be call.
