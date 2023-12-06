using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public interface IRefCounted : IDisposable
{
    #region Public APIs

    #region Properties

    bool IsDisposed { get; }

    int RefCounter { get; }

    #endregion

    #region Methods

    int AddRef();

    #endregion

    #endregion
}
