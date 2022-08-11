using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

[StructLayout(LayoutKind.Sequential)]
public struct ExclusiveAccessControl
{
    private int _data;

    public ExclusiveAccessControl()
    {
        _data = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryTakeControl()
    {
        return Interlocked.CompareExchange(ref _data, 1, 0) == 0;
    }
    
    public bool TakeControl(TimeSpan? wait)
    {
        if (Interlocked.CompareExchange(ref _data, 1, 0) == 0)
        {
            return true;
        }

        var bbb = new BurnBabyBurn(wait);
        while (bbb.Wait())
        {
            if (Interlocked.CompareExchange(ref _data, 1, 0) == 0)
            {
                return true;
            }
        }

        return false;
    }

    public void ReleaseControl()
    {
        _data = 0;
    }
}