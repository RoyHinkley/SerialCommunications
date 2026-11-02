using Xunit;

namespace SerialCommunications.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialHardwareCollection
{
    public const string Name = "Serial hardware";
}
