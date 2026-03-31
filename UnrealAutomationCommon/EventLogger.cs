using LocalAutomation.Runtime;

namespace UnrealAutomationCommon;

/// <summary>
/// Maintains the existing UnrealAutomationCommon logger type name while reusing the shared LocalAutomation event
/// logger implementation.
/// </summary>
public class EventLogger : EventStreamLogger
{
}
