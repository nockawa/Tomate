using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public struct ProfileThis
{
    #region Public APIs

    #region Properties

    public TimeSpan Duration { get; private set; }

    #endregion

    #region Methods

    public static implicit operator TimeSpan(ProfileThis pt) => pt.Duration;

    public static Handle Start(ref ProfileThis o) => new(ref o);

    #endregion

    #endregion

    #region Inner types

    public ref struct Handle
    {
        #region Public APIs

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Dispose()
        {
            var end = Stopwatch.GetTimestamp();
            _owner.Duration = TimeSpan.FromTicks(end - _start);
        }

        #endregion

        #endregion

        #region Fields

        private readonly ref ProfileThis _owner;
        private readonly long _start;

        #endregion

        #region Constructors

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Handle(ref ProfileThis owner)
        {
            _owner = ref owner;
            _start = Stopwatch.GetTimestamp();
        }

        #endregion
    }

    #endregion
}