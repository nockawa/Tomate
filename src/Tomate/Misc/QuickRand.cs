using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Quick random generation number
/// </summary>
/// <remarks>
/// Also known as the Marsaglia's xor-shift generator, it's very fast and pretty decent for the distribution
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class QuickRand
{
    #region Public APIs

    #region Methods

    /// <summary>
    /// Get the next randomly generated number
    /// </summary>
    /// <returns></returns>
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

    #endregion

    #endregion

    #region Fields

    // https://stackoverflow.com/a/1640399/802124
    static int randx = 123456789, randy = 362436069, randz = 521288629;

    #endregion
}