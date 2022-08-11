using System.Runtime.InteropServices;
using System.Text;

namespace Tomate;

public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    private const int Size = 64;
#pragma warning disable CS0649
    private fixed byte _data[Size];
#pragma warning restore CS0649

    /// <summary>
    /// Construct a String64 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <see cref="stringAddr"/> memory area</param>
    public String64(byte* stringAddr, int length = 64)
    {
        fixed (byte* a = _data)
        {
            var dest = new Span<byte>(a, 64);
            dest.Clear();                                       // Need to clear the whole zone because GetHashCode() does a 64bytes hash always
            new Span<byte>(stringAddr, length).CopyTo(dest);
        }
    }

    /// <summary>
    /// Construct a String64 instance from a string. THROW if the string is too big!
    /// </summary>
    /// <param name="source">The string to use as input. If the string's UTF8 equivalent is sized more than 63bytes it will throw an exception.</param>
    public String64(string source)
    {
        fixed (char* s = source)
        fixed (byte* a = _data)
        {
            var inLength = source.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(s, inLength);
            if (sizeRequired > 63) ThrowHelper.StringTooBigForString64(nameof(source), source);

            var l = Encoding.UTF8.GetBytes(s, inLength, a, 63);
            new Span<byte>(s, 64).Slice(l).Clear();     //Null terminator until the end
        }
    }

    public override string ToString() => AsString;

    public static implicit operator String64(string str) => new() { AsString = str };

    /// <summary>
    /// Get or set the content of the string. READ remarks!
    /// </summary>
    /// <remarks>
    /// Setting a new string WILL NOT throw if it is bigger, the string will be truncated.
    /// This behavior is intentionaly different from <see cref="String64(string)"/>
    /// </remarks>
    public string AsString
    {

        get
        {
            fixed (byte* a = _data)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(a));
            }
        }

        set
        {
            fixed (char* c = value)
            fixed (byte* a = _data)
            {
                var inLength = value.Length;
                var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
                if (sizeRequired < Size)
                {
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, 63);
                    new Span<byte>(a, 64).Slice(l).Clear();     //Null terminator until the end
                }
                else
                {
                    // Note: not wise to stackalloc with unknown size...
                    Span<byte> buffer = stackalloc byte[sizeRequired];
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size - 1] = 0;
                }
            }
        }
    }

    public int CompareTo(String64 other)
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 64).SequenceCompareTo(new Span<byte>(other._data, 64));
        }
    }

    public bool Equals(String64 other)
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 64).SequenceEqual(new Span<byte>(other._data, 64));
        }
    }

    public override bool Equals(object obj) => obj is String64 other && Equals(other);

    public override int GetHashCode()
    {
        fixed (byte* a = _data)
        {
            return (int)MurmurHash2.Hash(new Span<byte>(a, 64));
        }
    }

    public static bool operator ==(String64 left, String64 right) => left.Equals(right);

    public static bool operator !=(String64 left, String64 right) => !left.Equals(right);
}