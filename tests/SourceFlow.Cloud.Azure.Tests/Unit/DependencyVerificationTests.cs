using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using FsCheck;
using FsCheck.Xunit;
using Testcontainers.Azurite;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

/// <summary>
/// Verification tests to ensure all new testing dependencies are properly installed and accessible.
/// </summary>
public class DependencyVerificationTests
{
    [Fact]
    public void FsCheck_IsAvailable()
    {
        // Verify FsCheck is available for property-based testing
        var generator = Arb.Generate<int>();
        Assert.NotNull(generator);
    }

    [Property]
    public bool FsCheck_PropertyTest_Works(int value)
    {
        // Simple property test to verify FsCheck.Xunit integration
        return Math.Abs(value) >= 0; // Always true property
    }

    [Fact]
    public void BenchmarkDotNet_IsAvailable()
    {
        // Verify BenchmarkDotNet attributes are available
        var benchmarkType = typeof(BenchmarkAttribute);
        Assert.NotNull(benchmarkType);
    }

    [Fact]
    public void Azurite_TestContainer_IsAvailable()
    {
        // Verify Azurite test container is available
        var containerType = typeof(AzuriteContainer);
        Assert.NotNull(containerType);
    }

    [Fact]
    public void Azure_SDK_TestUtilities_AreAvailable()
    {
        // Verify Azure SDK test utilities are available
        Assert.NotNull(typeof(ServiceBusClient));
        Assert.NotNull(typeof(KeyClient));
        Assert.NotNull(typeof(SecretClient));
        Assert.NotNull(typeof(DefaultAzureCredential));
        Assert.NotNull(typeof(ArmClient));
    }

    [Fact]
    public void TestContainers_IsAvailable()
    {
        // Verify TestContainers base functionality is available
        var testContainersType = typeof(DotNet.Testcontainers.Containers.IContainer);
        Assert.NotNull(testContainersType);
    }
}