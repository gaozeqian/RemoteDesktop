using System.IO.Pipes;
using System.Security.Principal;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The local control pipe's two defences against another process getting hold of its well-known name: the server
/// refuses to share a name that is already taken, and the client connects at a level that does not let the server
/// act as the caller.
/// </summary>
public class WindowsPipesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPipeName() => $"RemoteDesktop.Pipes.Test.{Guid.NewGuid():N}";

    private static SecurityIdentifier[] Clients() => [WindowsPipes.AuthenticatedUsers];

    [Fact]
    public void A_first_instance_cannot_be_created_while_someone_else_holds_the_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are Windows only here.");

        var name = NewPipeName();

        // Whoever got there first: an ordinary instance, created without asking to be the first.
        using var squatter = WindowsPipes.CreateServer(name, Clients(), firstInstance: false);

        var error = Record.Exception(() => WindowsPipes.CreateServer(name, Clients(), firstInstance: true));

        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected the name to be refused, got: {error?.GetType().Name ?? "no exception"}.");
    }

    [Fact]
    public async Task The_first_instance_is_first_and_later_ones_accept_further_clients()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are Windows only here.");

        var name = NewPipeName();
        var servers = new List<NamedPipeServerStream>();
        var clients = new List<Stream>();
        try
        {
            // The order the service uses: the first instance of an attempt claims the name, and each connection is
            // followed by a new, non-first instance that waits for the next client.
            var server = WindowsPipes.CreateServer(name, Clients(), firstInstance: true);
            servers.Add(server);

            for (var i = 0; i < 3; i++)
            {
                var accepting = server.WaitForConnectionAsync(Ct);
                clients.Add(await HostControlEndpoint.ConnectAsync(name, TimeSpan.FromSeconds(5), Ct));
                await accepting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

                server = WindowsPipes.CreateServer(name, Clients(), firstInstance: false);
                servers.Add(server);
            }

            Assert.Equal(3, clients.Count);
            Assert.All(servers.Take(3), connected => Assert.True(connected.IsConnected));
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }

            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task The_client_connects_at_the_identification_level_and_can_still_talk_to_the_server()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are Windows only here.");

        var name = NewPipeName();
        await using var server = WindowsPipes.CreateServer(name, Clients(), firstInstance: true);

        var accepting = server.WaitForConnectionAsync(Ct);
        await using var client = await HostControlEndpoint.ConnectAsync(name, TimeSpan.FromSeconds(5), Ct);
        await accepting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The conversation itself still works in both directions.
        await client.WriteAsync("ping"u8.ToArray(), Ct);
        await client.FlushAsync(Ct);
        var request = new byte[4];
        await server.ReadExactlyAsync(request, Ct);
        Assert.Equal("ping"u8.ToArray(), request);

        // What the server can do with the caller's identity: at Identification it can see who it is, and nothing more.
        // (Windows only lets a server impersonate after it has read something from the client.)
        var level = TokenImpersonationLevel.None;
        server.RunAsClient(() => level = WindowsIdentity.GetCurrent().ImpersonationLevel);
        Assert.Equal(TokenImpersonationLevel.Identification, level);

        await server.WriteAsync("pong"u8.ToArray(), Ct);
        await server.FlushAsync(Ct);
        var answer = new byte[4];
        await client.ReadExactlyAsync(answer, Ct);
        Assert.Equal("pong"u8.ToArray(), answer);
    }
}
