namespace Tomate;

/// <summary>
/// Facade for an unmanaged based instance
/// </summary>
/// <remarks>
/// Each <c>struct</c> implementing this interface must follow a very specific constraint:
/// The data layout must be one field of type MemoryBlock and that's it!
/// Which means each instance of a given type must be allocated through a <see cref="IMemoryManager"/>.
/// </remarks>
public interface IUnmanagedFacade
{
    #region Public APIs

    #region Properties

    IMemoryManager MemoryManager { get; }
    MemoryBlock MemoryBlock { get; }

    #endregion

    #endregion
}
