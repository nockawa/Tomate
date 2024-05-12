using System.Diagnostics;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

// TODO MemBlock: AddRef/Dispose(release) can have a race condition
// If thread B is calling AddRef while thread A is freeing a block.
//  Need a lock mechanism in the MemBlock's header (interlocked.or/add in the RefCounter) to prevent this issue.
//  Maybe consider exposing the lock and using in the appropriate APIs.

/// <summary>
/// Represent an allocated block of unmanaged memory
/// </summary>
/// <remarks>
/// Use one of the implementation of <see cref="IMemoryManager"/> to allocate a block.
/// Each instance of <see cref="MemoryBlock"/> has an internal RefCounter with an initial value of 1, that can be incremented with <see cref="AddRef"/>.
/// Releasing ownership is done by calling <see cref="Dispose"/>, when the RefCounter reaches 0, the memory block is released and no longer usable.
/// Each Memory Block has a Header that precedes its starting address (<see cref="BlockReferential.GenBlockHeader"/>), the RefCounter resides in this header
/// as well as the Allocator that owns the block.
/// Freeing a Memory Block instance will redirect the operation to the appropriate Allocator.
/// </remarks>
[DebuggerDisplay("IsDefault: {IsDefault}, RefCounter: {RefCounter}, IsDisposed: {IsDisposed}, {MemorySegment}")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
[PublicAPI]
public struct MemoryBlock : IRefCounted
{
    #region Public APIs

    #region Properties

    /// <summary>
    /// If <c>true</c> the instance doesn't refer to a valid MemoryBlock
    /// </summary>
    public bool IsDefault => MemorySegment.IsDefault;

    /// <summary>
    /// If <c>true</c> the instance is not valid and considered as <c>Default</c> (<see cref="IsDefault"/> will be also <c>true</c>).
    /// </summary>
    public bool IsDisposed => MemorySegment.IsDefault;

    public IMemoryManager MemoryManager => BlockReferential.GetMemoryManager(this);

    /// <summary>
    /// Access the value of the Reference Counter
    /// </summary>
    /// <remarks>
    /// Increasing its value is done through <see cref="AddRef"/>, decreasing by calling <see cref="Dispose"/>. The MemoryBlock is freed when the counter
    /// reaches 0.
    /// </remarks>
    public unsafe int RefCounter
    {
        get
        {
            if (MemorySegment.IsDefault)
            {
                return 0;
            }
            var header = (BlockReferential.GenBlockHeader*)(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
            return header->RefCounter;
        }
    }

    #endregion

    #region Methods

    public static implicit operator MemorySegment(MemoryBlock mb) => mb.MemorySegment;
    public static implicit operator MemoryBlock(MemorySegment seg) => new(seg);

    /// <summary>
    /// Extend the MemoryBlock lifetime by incrementing its Reference Counter.
    /// </summary>
    /// <returns>The new Reference Counter</returns>
    /// <remarks>
    /// A MemoryBlock can be shared among multiple threads, the only way to guarantee ownership is to call <see cref="AddRef"/> to extend it and a matching
    /// <see cref="Dispose"/> to release it.
    /// </remarks>
    public unsafe int AddRef()
    {
        var header = (BlockReferential.GenBlockHeader*)(MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
        return Interlocked.Increment(ref header->RefCounter);
    }

    public MemoryBlock<T> Cast<T>() where T : unmanaged => new(MemorySegment.Cast<T>());

    /// <summary>
    /// Attempt to Dispose and free the MemoryBlock
    /// </summary>
    /// <remarks>
    /// If the instance is valid (<see cref="IsDefault"/> is <c>false</c>), it simply defer to <see cref="BlockReferential.Free"/>.
    /// If the <see cref="RefCounter"/> is equal to <c>1</c>, then the MemoryBlock will be freed.
    /// </remarks>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (BlockReferential.Free(this))
        {
            MemorySegment = default;
        }
    }

    /// <summary>
    /// Resize the MemoryBlock
    /// </summary>
    /// <param name="newSize">The new size, can't be more than <see cref="IMemoryManager.MaxAllocationLength"/>.</param>
    public void Resize(int newSize) => BlockReferential.Resize(ref this, newSize);

    #endregion

    #endregion

    #region Fields

    /// <summary>
    /// The Memory Segment corresponding to the Memory Block
    /// </summary>
    /// <remarks>
    /// There is also an implicit casting operator <see cref="op_Implicit(Tomate.MemoryBlock)"/> that has the same function.
    /// </remarks>
    public MemorySegment MemorySegment;

    #endregion

    #region Constructors

    /// <summary>
    /// Construct a MemoryBlock from a MemorySegment
    /// </summary>
    /// <param name="memorySegment">The segment with a starting address that matches the block</param>
    /// <remarks>
    /// The size of the MemorySegment should match the real size of the MemoryBlock, higher would likely result to crash, lesser would be unpredictable.
    /// </remarks>
    public MemoryBlock(MemorySegment memorySegment)
    {
        MemorySegment = memorySegment;
    }

    internal unsafe MemoryBlock(byte* address, int length, int mmfId)
    {
        MemorySegment = new MemorySegment(address, length, mmfId);
    }

    #endregion
}

[DebuggerDisplay("IsDefault: {IsDefault}, RefCounter: {RefCounter}, IsDisposed: {IsDisposed}, {MemorySegment}")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
[PublicAPI]
public struct MemoryBlock<T> : IRefCounted where T : unmanaged
{
    #region Public APIs

    #region Properties

    public bool IsDefault => MemorySegment.IsDefault;
    public bool IsDisposed => MemorySegment.IsDefault;

    public long MaxAllocationLength => MemoryManager?.MaxAllocationLength ?? 0;

    public IMemoryManager MemoryManager => BlockReferential.GetMemoryManager(this);

    public unsafe int RefCounter
    {
        get
        {
            var header = (BlockReferential.GenBlockHeader*)((byte*)MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
            return header->RefCounter;
        }
    }

    #endregion

    #region Methods

    public static implicit operator MemorySegment<T>(MemoryBlock<T> mb) => mb.MemorySegment;
    public static implicit operator MemoryBlock(MemoryBlock<T> mb) => new(mb.MemorySegment.Cast());
    public static implicit operator MemoryBlock<T>(MemorySegment<T> seg) => new(seg);

    public unsafe int AddRef()
    {
        var header = (BlockReferential.GenBlockHeader*)((byte*)MemorySegment.Address - sizeof(BlockReferential.GenBlockHeader));
        return Interlocked.Increment(ref header->RefCounter);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (BlockReferential.Free(this))
        {
            MemorySegment = default;
        }
    }

    /// <summary>
    /// Resize the MemoryBlock
    /// </summary>
    /// <param name="newSize">The new size, can't be more than <see cref="IMemoryManager.MaxAllocationLength"/>.</param>
    /// <remarks>This method will change the <see cref="MemorySegment"/> and its address.</remarks>
    public void Resize(int newSize) => BlockReferential.Resize(ref this, newSize);

    #endregion

    #endregion

    #region Fields

    public MemorySegment<T> MemorySegment;

    #endregion

    #region Constructors

    public MemoryBlock(MemorySegment<T> memorySegment)
    {
        MemorySegment = memorySegment;
    }

    #endregion
}