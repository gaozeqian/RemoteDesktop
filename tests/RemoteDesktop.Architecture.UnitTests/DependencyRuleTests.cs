using System.Reflection;
using NetArchTest.Rules;

namespace RemoteDesktop.Architecture.UnitTests;

/// <summary>Enforces the layering of the design: dependencies only point inwards, platforms only see the ports.</summary>
public class DependencyRuleTests
{
    private const string Domain = "RemoteDesktop.Domain";
    private const string Abstractions = "RemoteDesktop.Abstractions";
    private const string Protocol = "RemoteDesktop.Protocol";
    private const string Signaling = "RemoteDesktop.Signaling";
    private const string Transport = "RemoteDesktop.Transport";
    private const string Application = "RemoteDesktop.Application";
    private const string Features = "RemoteDesktop.Features";
    private const string Media = "RemoteDesktop.Media";
    private const string Platform = "RemoteDesktop.Platform";
    private const string Server = "RemoteDesktop.Server";
    private const string Client = "RemoteDesktop.Client";
    private const string HostService = "RemoteDesktop.HostService";
    private const string SessionAgent = "RemoteDesktop.SessionAgent";

    private static readonly string[] Outer = [Features, Media, Platform, Server, Client, HostService, SessionAgent];

    public static TheoryData<string, string[]> Rules => new()
    {
        { Domain, [Abstractions, Protocol, Signaling, Transport, Application, .. Outer] },
        { Abstractions, [Protocol, Signaling, Transport, Application, .. Outer] },
        { Protocol, [Signaling, Transport, Application, .. Outer] },
        { Signaling, [Domain, Abstractions, Protocol, Transport, Application, .. Outer] },
        { Transport, [Application, .. Outer] },
        { Application, Outer },
        { Media, [Protocol, Signaling, Transport, Application, Features, Platform, Server, Client, HostService, SessionAgent] },
        { Platform, [Protocol, Signaling, Transport, Application, Features, Media, Server, Client, HostService, SessionAgent] },
        { Server, [Abstractions, Protocol, Transport, Application, Features, Media, Platform, Client, HostService, SessionAgent] },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Layer_does_not_depend_on_forbidden_layers(string layer, string[] forbidden)
    {
        var result = Types.InAssemblies(LoadAssemblies(layer))
            .Should().NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, $"{layer} violates layering: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>
    /// The feature modules, listed once. Everything below derives from this: an earlier version repeated the list in
    /// the [InlineData] attributes and again in the method body, so a fifth feature added to only one of the two
    /// produced either no test for it at all or one whose <c>others</c> could never fail.
    /// </summary>
    private static readonly string[] FeatureModules = ["Camera", "Desktop", "FileTransfer", "Terminal"];

    public static TheoryData<string> FeatureModuleNames()
    {
        var names = new TheoryData<string>();
        foreach (var module in FeatureModules)
        {
            names.Add(module);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(FeatureModuleNames))]
    public void Feature_modules_do_not_depend_on_each_other(string feature)
    {
        var others = FeatureModules.Where(f => f != feature).Select(f => $"{Features}.{f}").ToArray();

        var result = Types.InAssembly(Assembly.Load($"{Features}.{feature}"))
            .Should().NotHaveDependencyOnAny(others)
            .GetResult();

        Assert.True(result.IsSuccessful, $"{Features}.{feature} depends on another feature: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>Loads every assembly next to the tests whose name starts with <paramref name="prefix"/>.</summary>
    private static Assembly[] LoadAssemblies(string prefix)
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, prefix + "*.dll")
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .ToArray();

        Assert.NotEmpty(assemblies);
        return assemblies;
    }
}
