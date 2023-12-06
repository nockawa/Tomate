using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public interface IProcessProvider
{
    #region Public APIs

    #region Properties

    public static IProcessProvider Singleton { get; set; } = new WindowsProcessProvider();

    int CurrentProcessId { get; }

    #endregion

    #region Methods

    bool IsProcessAlive(int processId);

    #endregion

    #endregion
}