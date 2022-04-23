﻿using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

internal static class SpanHelpers
{
    public static Span<TTo> Cast<TFRom, TTo>(this Span<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
    public static ReadOnlySpan<TTo> Cast<TFRom, TTo>(this ReadOnlySpan<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
}

public static class DisposeExtensions
{
    public static void DisposeAndNull<T>(this object owner, ref T obj) where T : class, IDisposable
    {
        if (owner == null) return;
        if (obj == null) return;
        obj.Dispose();
        obj = null;
    }
}

public static class PaddingExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Pad4(this int v) => v + 3 & -4;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Pad8(this int v) => v + 7 & -8;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Pad16(this int v) => v + 15 & -16;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Pad32(this int v) => v + 31 & -32;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static unsafe int Pad<T>(this int v) where T : unmanaged => (v + sizeof(T) - 1) & -sizeof(T);
}

public static class StoreInExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int SizeInLong(this ushort bytes) => (bytes + 7) / 8;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int SizeInLong(this int bytes) => (bytes + 7) / 8;
}

public static class PackExtensions
{
    // 16-bits with unsigned
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref ushort n, byte high, byte low) => n = (ushort)(high << 8 | low);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (byte, byte) Unpack(this ushort n) => ((byte)(n >> 8), (byte)(n & 0xFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte High(this ushort n) => (byte)(n >> 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte Low(this ushort n) => (byte)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref ushort n, byte val) => n = (ushort)(val << 8 | (n & 0xFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref ushort n, byte val) => n = (ushort)((n & 0xFF00) | val);

    // 16-bits with signed
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref ushort n, sbyte high, sbyte low) => n = (ushort)(high << 8 | (byte)low);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (sbyte, sbyte) UnpackS(this ushort n) => ((sbyte)(n >> 8), (sbyte)(n & 0xFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static sbyte HighS(this ushort n) => (sbyte)(n >> 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static sbyte LowS(this ushort n) => (sbyte)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref ushort n, sbyte val) => n = (ushort)(val << 8 | (n & 0xFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref ushort n, sbyte val) => n = (ushort)((n & 0xFF00) | (byte)val);

    // 32-bits with unsigned
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref uint n, ushort high, ushort low) => n = (uint)high << 16 | low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref int n, ushort high, ushort low) => n = (int)high << 16 | low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (ushort, ushort) Unpack(this uint n) => ((ushort)(n >> 16), (ushort)(n & 0xFFFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ushort High(this uint n) => (ushort)(n >> 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ushort Low(this uint n) => (ushort)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref uint n, ushort val) => n = (uint)val << 16 | (n & 0xFFFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref uint n, ushort val) => n = (n & 0xFFFF0000) | val;

    // 32-bits with signed
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref uint n, short high, short low) => n = (uint)high << 16 | (ushort)low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref int n, short high, short low) => n = (int)high << 16 | (ushort)low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (short, short) UnpackS(this uint n) => ((short)(n >> 16), (short)(n & 0xFFFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static short HighS(this uint n) => (short)(n >> 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static short LowS(this uint n) => (short)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref uint n, short val) => n = (uint)val << 16 | (n & 0xFFFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref uint n, short val) => n = (n & 0xFFFF0000) | (ushort)val;

    // 64-bits with unsigned
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref ulong n, uint high, uint low) => n = (ulong)high << 32 | low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (uint, uint) Unpack(this ulong n) => ((uint)(n >> 32), (uint)(n & 0xFFFFFFFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint High(this ulong n) => (uint)(n >> 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint Low(this ulong n) => (uint)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref ulong n, uint val) => n = (ulong)val << 32 | (n & 0xFFFFFFFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref ulong n, uint val) => n = (n & 0xFFFFFFFF00000000) | val;

    // 64-bits with signed
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack(this ref ulong n, int high, int low) => n = (ulong)high << 32 | (uint)low;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) UnpackS(this ulong n) => ((int)(n >> 32), (int)(n & 0xFFFFFFFF));
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int HighS(this ulong n) => (int)(n >> 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int LowS(this ulong n) => (int)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void High(this ref ulong n, int val) => n = (ulong)val << 32 | (n & 0xFFFFFFFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Low(this ref ulong n, int val) => n = (n & 0xFFFFFFFF00000000) | (uint)val;
}

public static class MathExtensions
{
    public static bool IsPowerOf2(this int x) => (x & (x - 1)) == 0;
    public static bool IsPowerOf2(this long x) => (x & (x - 1)) == 0;

    public static double TotalSeconds(this int ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    public static double TotalSeconds(this long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;

    public static string FriendlyTime(this double elapsed, bool displayRate = true)
    {
        var scalesE = new[] { "sec", "ms", "µs", "ns" };
        var e = elapsed;
        var iE = 0;
        for (; iE < 3; iE++)
        {
            if (Math.Abs(e) > 1)
            {
                break;
            }

            e *= 1000;
        }

        if (displayRate)
        {
            var scalesF = new[] { "", "K", "M", "B" };
            var f = 1 / elapsed;
            var iF = 0;
            for (; iF < 3; iF++)
            {
                if (f < 1000)
                {
                    break;
                }

                f /= 1000;
            }
            return $"{e:0.###}{scalesE[iE]} ({f:0.###}{scalesF[iF]}/sec)";
        }
        else
        {
            return $"{e:0.###}{scalesE[iE]}";
        }
    }
    public static string FriendlySize(this long val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return $"{f:0.###}{scalesF[iF]}";
    }

    public static string FriendlySize(this int val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return $"{f:0.###}{scalesF[iF]}";
    }

    public static string FriendlySize(this double val)
    {
        var scalesF = new[] { "b", "Kb", "Mb", "Gb" };
        var f = val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return $"{f:0.###}{scalesF[iF]}";
    }

    public static string Bandwidth(int size, double elapsed)
    {
        return $"{(size / elapsed).FriendlySize()}/sec";
    }

    public static string Bandwidth(long size, double elapsed)
    {
        return $"{(size / elapsed).FriendlySize()}/sec";
    }
}
