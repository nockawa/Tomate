using System.Text;

namespace Tomate;

public struct StringTable : IDisposable
{
    private AppendCollection<byte> _storage;

    public static StringTable Create(IPageAllocator allocator, int pageCapacity)
    {
        return new StringTable(allocator, pageCapacity, true);
    }
    private StringTable(IPageAllocator allocator, int pageCapacity, bool create)
    {
        _storage = AppendCollection<byte>.Create(allocator, pageCapacity);
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
}