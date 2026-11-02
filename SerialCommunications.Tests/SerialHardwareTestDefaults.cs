using System.Runtime.CompilerServices;

namespace SerialCommunications.Tests;

/// <summary>
/// Local defaults for the connected real-port test pair. Basic real-port and hardware-flow-control integration
/// tests are enabled; operator-coordinated physical-loss tests remain separately opt-in.
/// </summary>
internal static class SerialHardwareTestDefaults
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_DUT_PORT", "COM6");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_PEER_PORT", "COM7");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_TEST_RTS_CTS")))
            Environment.SetEnvironmentVariable("SERIAL_TEST_RTS_CTS", "1");
    }
}
