using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public interface IRefCounted : IDisposable
{
    int AddRef();
    int RefCounter { get; }
    
    bool IsDisposed { get; }
}
