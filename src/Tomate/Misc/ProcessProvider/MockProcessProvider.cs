using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public class MockProcessProvider : IProcessProvider
{
    #region Public APIs

    #region Properties

    // For test purpose, the ProcessId can be overriden per thread, with a fallback to DefaultProcessId
    public int CurrentProcessId
    {
        get => _currentProcessId.IsValueCreated ? _currentProcessId.Value : DefaultProcessId;
        set
        {
            if (_currentProcessId.IsValueCreated)
            {
                _processes.Remove(_currentProcessId.Value);
            }
            _currentProcessId.Value = value;
            _processes.Add(value);
        }
    }

    public int DefaultProcessId
    {
        get => (_defaultProcessId == 0) ? Environment.ProcessId : _defaultProcessId;
        set => _defaultProcessId = value;
    }

    #endregion

    #region Methods

    public bool IsProcessAlive(int processId) => processId==Environment.ProcessId || _processes.Contains(processId);

    public void UnregisterProcess(int secondProcessId)
    {
        _processes.Remove(secondProcessId);
    }

    #endregion

    #endregion

    #region Fields

    private ThreadLocal<int> _currentProcessId = new();
    private int _defaultProcessId;

    private HashSet<int> _processes = new();

    #endregion
}