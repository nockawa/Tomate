using System.Diagnostics;

namespace Tomate;

public class WindowsProcessProvider : IProcessProvider
{
    #region Public APIs

    #region Properties

    public int CurrentProcessId => Environment.ProcessId;

    #endregion

    #region Methods

    public bool IsProcessAlive(int processId)
    {
        try
        {
            Process.GetProcessById(processId);
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    #endregion

    #endregion
}