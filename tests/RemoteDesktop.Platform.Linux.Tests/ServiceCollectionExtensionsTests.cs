using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Linux;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// The composition root of the platform layer: what a host gets when it asks for the Linux ports.
/// </summary>
public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private const string LinuxOnly = "The Linux platform layer.";

    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("rd-linux-").FullName;

    public void Dispose() => Directory.Delete(_dataDirectory, recursive: true);

    [Fact]
    public void Every_port_the_platform_registers_can_actually_be_created()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var services = new ServiceCollection();
        // Hosts always add logging, and a port that takes a logger cannot be built without a provider.
        services.AddLogging();
        // Only what the platform adds is resolved below: AddLogging registers open generics such as IOptions<> that
        // cannot be asked for by their open type, and those are not this test's business.
        var alreadyPresent = services.Select(d => (d.ServiceType, d.ServiceKey)).ToHashSet();

        services.AddLinuxPlatform(_dataDirectory);
        var added = services.Where(d => !alreadyPresent.Contains((d.ServiceType, d.ServiceKey))).ToList();
        Assert.NotEmpty(added);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // ValidateOnBuild only proves the graph can be described; resolving proves each one can be built. A port whose
        // constructor throws would otherwise pass registration and fail the first time a host starts.
        foreach (var descriptor in added)
        {
            Assert.NotNull(provider.GetRequiredService(descriptor.ServiceType));
        }
    }

    [Fact]
    public void The_ports_a_host_needs_are_registered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var services = new ServiceCollection();
        // Hosts always add logging, and a port that takes a logger cannot be built without a provider.
        services.AddLogging();
        services.AddLinuxPlatform(_dataDirectory);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ISecretStore>());
        Assert.NotNull(provider.GetService<IUserContextGuard>());
    }

    [Fact]
    public void No_agent_gateway_is_registered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var services = new ServiceCollection();
        // Hosts always add logging, and a port that takes a logger cannot be built without a provider.
        services.AddLogging();
        services.AddLinuxPlatform(_dataDirectory);

        using var provider = services.BuildServiceProvider();

        // There is no Session 0 here, so the service-plus-agent split that exists on Windows has no counterpart: the
        // host runs as the signed-in user inside that user's session and serves the feature channels itself. Registering
        // a gateway would make the host wait for an agent that is never launched.
        Assert.Null(provider.GetService<IAgentGateway>());
    }
}
