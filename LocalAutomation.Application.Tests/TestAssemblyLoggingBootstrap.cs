using System.Runtime.CompilerServices;
using TestUtilities;

namespace LocalAutomation.Application.Tests;

/// <summary>
/// Initializes the shared MEL test logger pipeline for the application test assembly.
/// </summary>
internal static class TestAssemblyLoggingBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        _ = TestLoggingBootstrap.LoggerFactory;
    }
}
