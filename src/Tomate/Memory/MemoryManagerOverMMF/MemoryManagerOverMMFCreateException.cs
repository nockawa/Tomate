namespace Tomate;

public class MemoryManagerOverMMFCreateException : Exception
{
    public MemoryManagerOverMMFCreateException(string msg, Exception innerException) : base(msg, innerException) { }
}