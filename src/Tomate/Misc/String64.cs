using System.Runtime.InteropServices;
using System.Text;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Stores a string using UTF8 format with a max size of 64-bytes.
/// </summary>
/// <remarks>
/// This struct stores 64 bytes as a fixed array of byte, the string is stores in the buffer encoded with UTF8.
/// So the size of this struct is fixed and always of 64 bytes.
/// </remarks>
[PublicAPI]
public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    #region Constants

    private const int Size = 64;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Get or set the content of the string. READ remarks!
    /// </summary>
    /// <remarks>
    /// Setting a new string WILL NOT throw if it is bigger, the string will be truncated.
    /// This behavior is intentionally different from <see cref="String64(string)"/> constructor.
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
                    Span<byte> buffer = stackalloc byte[sizeRequired];          // TODO rework with stackalloc not failing when the string is too big 
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size - 1] = 0;
                }
            }
        }
    }

    #endregion

    #region Methods

    public static bool operator ==(String64 left, String64 right) => left.Equals(right);

    /// <summary>
    /// Cast a string to <see cref="String64"/>.
    /// </summary>
    /// <param name="str">The string to cast</param>
    /// <returns>The <see cref="String64"/> instance.</returns>
    public static implicit operator String64(string str) => new() { AsString = str };

    public static bool operator !=(String64 left, String64 right) => !left.Equals(right);

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

    /// <summary>
    /// Compute the hash code of the string instance
    /// </summary>
    /// <returns>The hash code</returns>
    /// <remarks>
    /// The <see cref="MurmurHash2"/> hashing is used.
    /// </remarks>
    public override int GetHashCode()
    {
        fixed (byte* a = _data)
        {
            return (int)MurmurHash2.Hash(new Span<byte>(a, 64));
        }
    }

    public override string ToString() => AsString;

    #endregion

    #endregion

    #region Fields

#pragma warning disable CS0649
    private fixed byte _data[Size];
#pragma warning restore CS0649

    #endregion

    #region Constructors

    /// <summary>
    /// Construct a String64 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <paramref name="stringAddr"/> memory area</param>
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
    /// <exception cref="ArgumentOutOfRangeException"> is thrown if the <paramref name="source"/> contains a string that exceed the max size of 63 bytes.
    /// </exception>
    public String64(string source)
    {
        fixed (char* s = source)
        fixed (byte* a = _data)
        {
            var inLength = source.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(s, inLength);
            if (sizeRequired > 63) ThrowHelper.StringTooBigForString64(nameof(source), source);

            var l = Encoding.UTF8.GetBytes(s, inLength, a, 63);
            new Span<byte>(a, 64).Slice(l).Clear();     //Null terminator until the end
        }
    }

    #endregion
}