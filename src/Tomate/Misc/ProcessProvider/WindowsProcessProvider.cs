using System.Diagnostics;

namespace Tomate;

public class WindowsProcessProvider : IProcessProvider
{
    private readonly HashSet<int> _sessions = new();
    public int ProcessProviderId { get; set; }
    public int CurrentProcessId => Environment.ProcessId;
    public bool RegisterProcess(int processId) => processId!=0 && _sessions.Add(processId);

    public bool UnregisterProcess(int processId) => _sessions.Remove(processId);

    public IEnumerable<int> GetAllRegisteredProcesses => _sessions;
    
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
}