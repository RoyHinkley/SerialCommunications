using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SerialCommunications.Tests;

/// <summary>
/// Test-assembly configuration. Real-port integration tests remain opt-in through their
/// SERIAL_TEST_* environment variables so the ordinary suite is independent of local hardware.
/// </summary>
internal static class SerialHardwareTestDefaults
{
}
