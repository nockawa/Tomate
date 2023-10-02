using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public struct ProfileThis
{
    public ref struct Handle
    {
        private readonly ref ProfileThis _owner;
        private readonly long _start;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Handle(ref ProfileThis owner)
        {
            _owner = ref owner;
            _start = Stopwatch.GetTimestamp();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Dispose()
        {
            var end = Stopwatch.GetTimestamp();
            _owner.Duration = TimeSpan.FromTicks(end - _start);
        }
    }

    public static Handle Start(ref ProfileThis o) => new(ref o);
    
    public TimeSpan Duration { get; private set; }
    public static implicit operator TimeSpan(ProfileThis pt) => pt.Duration;
}