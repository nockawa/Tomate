﻿using System.Text;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public struct MappedStringTable : IDisposable
{
    #region Public APIs

    #region Methods

    public static MappedStringTable Create(IPageAllocator allocator, int pageCapacity)
    {
        return new MappedStringTable(allocator, pageCapacity, true);
    }

    public unsafe int AddString(string str)
    {
        fixed (char* s = str)
        {
            var inLength = str.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(s, inLength);
            var seg = _storage.Reserve(sizeRequired + 1, out var res);

            Encoding.UTF8.GetBytes(s, inLength, seg.Address, seg.Length);
            seg[inLength] = 0;
            return res;
        }
    }

    public void Dispose()
    {
        _storage.Dispose();
    }

    #endregion

    #endregion

    #region Fields

    private MappedAppendCollection<byte> _storage;

    #endregion

    #region Constructors

    private MappedStringTable(IPageAllocator allocator, int pageCapacity, bool create)
    {
        _storage = MappedAppendCollection<byte>.Create(allocator, pageCapacity);
    }

    #endregion
}