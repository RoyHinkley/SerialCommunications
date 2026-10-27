using System.Runtime.CompilerServices;

namespace SerialCommunications.Tests;

/// <summary>Provides local defaults for the ordinary real-transport integration-test pair.</summary>
internal static class SerialHardwareTestDefaults
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_DUT_PORT", "COM3");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_PEER_PORT", "COM4");
    }
}
