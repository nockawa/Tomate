using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tomate;

public partial class DefaultMemoryManager
{
    private unsafe class NativeBlockInfo : IDisposable
    {
        private readonly int _blockSize;
        private readonly int _blockCapacity;
        private byte* _alignedAddress;
        private byte[] _array;

        private int _curFreeIndex;

        public NativeBlockInfo(int blockSize, int capacity)
        {
            var nativeBlockSize = blockSize * capacity;
            Debug.Assert((nativeBlockSize + 63) <= Array.MaxLength);
            _blockSize = blockSize;
            _blockCapacity = capacity;
            _array = GC.AllocateUninitializedArray<byte>(nativeBlockSize + 63, true);
            var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_array, 0).ToPointer();

            var offsetToAlign64 = (int)(((long)baseAddress + 63L & -64L) - (long)baseAddress);
            _alignedAddress = baseAddress + offsetToAlign64;

            _curFreeIndex = -1;
        }

        public MemorySegment DataSegment => new(_alignedAddress, _blockSize * _blockCapacity);

        public bool GetBlockSegment(out MemorySegment block)
        {
            var blockIndex = Interlocked.Increment(ref _curFreeIndex);
            if (blockIndex >= _blockCapacity)
            {
                block = default;
                return false;
            }
            block = new MemorySegment(_alignedAddress + _blockSize * blockIndex, _blockSize);
            return true;
        }

        public void Dispose()
        {
            _array = null;
            _alignedAddress = null;
        }
    }
}