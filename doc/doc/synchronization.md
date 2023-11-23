---
uid: synchronization-overview
---

# Synchronization overview

## Why?
The OS and .NET offer many mechanisms to take care about synchronization between threads and processes.
The traditional `lock` of .net implemented by the [monitor](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor) class is doing a very good job but is general purpose.

🍅 implements custom types with specific features to allows very low latency synchronization between threads and/or processes. 
These types are also designed to take full advantage of multi-core CPU and very short locking span.

## AccessControl
The [AccessControl](<xref:Tomate.AccessControl>) type allow threads to share the usage of a particular resource.