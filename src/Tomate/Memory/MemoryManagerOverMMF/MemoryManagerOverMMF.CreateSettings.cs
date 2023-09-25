using JetBrains.Annotations;

namespace Tomate;

public partial class MemoryManagerOverMMF
{
    [PublicAPI]
    public struct CreateSettings
    {
        public string FilePathName { get; }
        public string Name { get; }
        public long FileSize { get; }
        public int PageSize { get; }
        public bool CompactOnFinalClose { get; set; }
        public int MaxSessionCount { get; set; }
        public int MaxConcurrencyCount { get; set; }
        public IProcessProvider ProcessProvider { get; set; }

        internal bool IsCreate { get; }
        
        public CreateSettings(string filePathName, string name, long fileSize, int pageSize, 
            bool compactOnFinalClose=true, int maxSessionCount=8, int maxConcurrencyCount=8)
        {
            FilePathName = filePathName;
            Name = name;
            FileSize = fileSize;
            PageSize = pageSize;
            CompactOnFinalClose = compactOnFinalClose;
            MaxSessionCount = maxSessionCount;
            MaxConcurrencyCount = maxConcurrencyCount;
            IsCreate = true;
            ProcessProvider = new WindowsProcessProvider();
        }

        internal CreateSettings(string filePathName, string name)
        {
            FilePathName = filePathName;
            Name = name;
            IsCreate = false;
            ProcessProvider = new WindowsProcessProvider();
        }
    }
}