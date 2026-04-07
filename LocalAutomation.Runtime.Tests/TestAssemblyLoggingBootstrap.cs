using System.Runtime.CompilerServices;
using TestUtilities;

namespace LocalAutomation.Runtime.Tests;

/// <summary>
/// Initializes the shared MEL test logger pipeline for the runtime test assembly.
/// </summary>
internal static class TestAssemblyLoggingBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        _ = TestLoggingBootstrap.LoggerFactory;
    }
}
