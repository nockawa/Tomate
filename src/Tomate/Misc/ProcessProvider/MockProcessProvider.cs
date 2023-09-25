using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public class MockProcessProvider : IProcessProvider
{
    public int ProcessProviderId { get; set; }

    public int DefaultProcessId
    {
        get => (_defaultProcessId == 0) ? Environment.ProcessId : _defaultProcessId;
        set => _defaultProcessId = value;
    }

    // For test purpose, the ProcessId can be overriden per thread, with a fallback to DefaultProcessId
    public int CurrentProcessId
    {
        get => _currentProcessId.IsValueCreated ? _currentProcessId.Value : DefaultProcessId;
        set => _currentProcessId.Value = value;
    }

    private HashSet<int> _processes = new();
    private ThreadLocal<int> _currentProcessId = new();
    private int _defaultProcessId;

    public bool RegisterProcess(int processId) => processId != 0 && _processes.Add(processId);

    public bool UnregisterProcess(int processId) => _processes.Remove(processId);
    public IEnumerable<int> GetAllRegisteredProcesses => _processes;
    public bool IsProcessAlive(int processId) => _processes.Contains(processId);
}