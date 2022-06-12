﻿using System.Diagnostics;

namespace Tomate;

/// <summary>
/// Rewrite the content of the view by processing its data
/// </summary>
public struct RewriteMemoryView
{
    public readonly MemorySegment MemorySegment;

    private int _readPosition;
    private int _writePosition;

    public int ReadPosition => _readPosition;
    public int WritePosition => _writePosition;

    public RewriteMemoryView(MemorySegment memorySegment) : this()
    {
        MemorySegment = memorySegment;
    }

    public unsafe bool Fetch<T>(MemorySegment<T> dest) where T : unmanaged
    {
        var length = dest.Length * sizeof(T);
        if (_readPosition + length > MemorySegment.Length)
        {
            return false;
        }

        var pos = _readPosition;
        _readPosition += length;

        MemorySegment.Slice(pos, length).ToSpan<T>().CopyTo(dest);

        return true;
    }

    public unsafe MemorySegment<T> Reserve<T>(int length) where T : unmanaged
    {
        var lengthBytes = length * sizeof(T);
        if (_writePosition + lengthBytes > _readPosition)
        {
            return MemorySegment<T>.Empty;
        }
        
        var pos = _writePosition;
        _writePosition += lengthBytes;
        Debug.Assert(_writePosition <= MemorySegment.Length);
        return MemorySegment.Slice(pos, lengthBytes).Cast<T>();
    }
}