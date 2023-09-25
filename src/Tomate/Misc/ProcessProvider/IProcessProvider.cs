using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public interface IProcessProvider
{
    private static readonly Dictionary<int, IProcessProvider> Providers = new();
    private static int _nextProviderId = 0;

    public static int RegisterProcessProvider(IProcessProvider provider)
    {
        var ppId = _nextProviderId++;
        provider.ProcessProviderId = ppId;
        Providers.Add(ppId, provider);
        return ppId;
    }

    public static IProcessProvider GetProvider(int processProviderId)
    {
        Providers.TryGetValue(processProviderId, out var processProvider);
        return processProvider;
    }

    public static bool UnregisterProcessProvider(IProcessProvider processProvider)
    {
        return Providers.Remove(processProvider.ProcessProviderId);
    }
    
    int ProcessProviderId { get; set; }
    int CurrentProcessId { get; }
    bool RegisterProcess(int processId);
    bool UnregisterProcess(int processId);
    IEnumerable<int> GetAllRegisteredProcesses { get; }
    bool IsProcessAlive(int processId);
}