using System.Runtime.InteropServices;
using System.Text;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Stores a string using UTF8 format with a max size of 256-bytes.
/// </summary>
/// <remarks>
/// This struct stores 256 bytes as a fixed array of byte, the string is stores in the buffer encoded with UTF8.
/// So the size of this struct is fixed and always of 256 bytes.
/// </remarks>
[PublicAPI]
public unsafe struct String256 : IComparable<String256>, IEquatable<String256>
{
    #region Constants

    private const int Size = 256;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Get or set the content of the string. READ remarks!
    /// </summary>
    /// <remarks>
    /// Setting a new string WILL NOT throw if it is bigger, the string will be truncated.
    /// This behavior is intentionally different from <see cref="String256(string)"/> constructor.
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
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, 255);
                    new Span<byte>(a, 256).Slice(l).Clear();     //Null terminator until the end
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

    public static String256* Map(string source, void* address)
    {
        fixed (char* s = source)
        {
            var inLength = source.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(s, inLength);
            if (sizeRequired > 255) ThrowHelper.StringTooBigForString256(nameof(source), source);

            var l = Encoding.UTF8.GetBytes(s, inLength, (byte*)address, 255);
            new Span<byte>(address, 256).Slice(l).Clear();     //Null terminator until the end
        }
        return (String256*)address;
    }

    public static bool operator ==(String256 left, String256 right) => left.Equals(right);

    /// <summary>
    /// Cast a string to <see cref="String256"/>.
    /// </summary>
    /// <param name="str">The string to cast</param>
    /// <returns>The <see cref="String256"/> instance.</returns>
    public static implicit operator String256(string str) => new() { AsString = str };

    public static bool operator !=(String256 left, String256 right) => !left.Equals(right);

    public int CompareTo(String256 other)
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 256).SequenceCompareTo(new Span<byte>(other._data, 256));
        }
    }

    public bool Equals(String256 other)
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 256).SequenceEqual(new Span<byte>(other._data, 256));
        }
    }

    public override bool Equals(object obj) => obj is String256 other && Equals(other);

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
            return (int)MurmurHash2.Hash(new Span<byte>(a, 256));
        }
    }

    public override string ToString() => AsString;

    #endregion

    #endregion

    #region Constructors

    /// <summary>
    /// Construct a String256 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <paramref name="stringAddr"/> memory area</param>
    public String256(byte* stringAddr, int length = 256)
    {
        fixed (byte* a = _data)
        {
            var dest = new Span<byte>(a, 256);
            dest.Clear();                                       // Need to clear the whole zone because GetHashCode() does a 256bytes hash always
            new Span<byte>(stringAddr, length).CopyTo(dest);
        }
    }

    /// <summary>
    /// Construct a String256 instance from a string. THROW if the string is too big!
    /// </summary>
    /// <param name="source">The string to use as input. If the string's UTF8 equivalent is sized more than 255bytes it will throw an exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"> is thrown if the <paramref name="source"/> contains a string that exceed the max size of 255 bytes.
    /// </exception>
    public String256(string source)
    {
        fixed (char* s = source)
        fixed (byte* a = _data)
        {
            var inLength = source.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(s, inLength);
            if (sizeRequired > 255) ThrowHelper.StringTooBigForString256(nameof(source), source);

            var l = Encoding.UTF8.GetBytes(s, inLength, a, 255);
            new Span<byte>(a, 256).Slice(l).Clear();     //Null terminator until the end
        }
    }

    #endregion

    #region Privates

    #region Fields

#pragma warning disable CS0649
    private fixed byte _data[Size];
#pragma warning restore CS0649

    #endregion

    #endregion
}