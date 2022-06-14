using System.Runtime.CompilerServices;

namespace Tomate;

public static class QuickRand
{
    // https://stackoverflow.com/a/1640399/802124
    static int randx = 123456789, randy = 362436069, randz = 521288629;

    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    public static int Next()
    {
        int t;
        randx ^= randx << 16;
        randx ^= randx >> 5;
        randx ^= randx << 1;

        t = randx;
        randx = randy;
        randy = randz;
        randz = t ^ randx ^ randy;

        return randz;
    }

}