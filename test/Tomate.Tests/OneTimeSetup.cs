using JetBrains.Annotations;
using NUnit.Framework;
using Serilog;

namespace Tomate.Tests;

[SetUpFixture]
[PublicAPI]
public class OneTimeSetup
{
    [OneTimeSetUp]
    public void Setup()
    {
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Verbose()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Seq("http://localhost:5341")
            .WriteTo.Console()
            .CreateLogger();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Log.CloseAndFlush();
    }
    
    public static bool IsRunningUnderDotCover()
    {
        return Environment.GetEnvironmentVariable("RESHARPER_TESTRUNNER") == "Cover";
    }    

}