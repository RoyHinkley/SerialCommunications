using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SerialCommunications.Tests;

/// <summary>Provides local defaults for the real-transport integration-test pair.</summary>
internal static class SerialHardwareTestDefaults
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_DUT_PORT", "COM3");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_PEER_PORT", "COM4");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_PHYSICAL_LOSS")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_PHYSICAL_LOSS", "1");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_RTS_CTS")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_RTS_CTS", "1");
    }
}
