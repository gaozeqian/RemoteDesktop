using System.Text;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The socket the UI and the background service talk over. It is a Unix domain socket here rather than the named
/// pipe Windows uses, and the address has to stay inside the per-user runtime directory — that directory is the
/// whole access control, so a socket that landed anywhere else would be open to every account on the machine.
/// </summary>
public sealed class HostControlEndpointTests
{
    private const string LinuxOnly = "A Unix domain socket, and XDG_RUNTIME_DIR.";

    [Fact]
    public void A_plain_name_becomes_a_socket_beside_the_other_runtime_state()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var address = HostControlEndpoint.Resolve("RemoteDesktop.HostControl");

        Assert.Equal(".sock", Path.GetExtension(address));
        Assert.True(Path.IsPathRooted(address), $"'{address}' is not an absolute path.");

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime))
        {
            Assert.StartsWith(runtime, address, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_address_given_as_a_path_is_used_as_it_is()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The --HostControlPipe option is how a developer points a second instance somewhere else, and on this
        // platform a path is the natural way to say that.
        Assert.Equal("/tmp/dev.sock", HostControlEndpoint.Resolve("/tmp/dev.sock"));
    }

    [Fact]
    public async Task A_message_survives_the_trip_from_one_end_to_the_other()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var address = Path.Combine(Directory.CreateTempSubdirectory("rd-ctl-").FullName, "host-control.sock");
        await using var listener = new HostControlSocketListener(address);

        var accepted = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();

        await using var client = await HostControlEndpoint.ConnectAsync(address, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await using var server = await accepted;

        var sent = Encoding.UTF8.GetBytes("status\n");
        await client.WriteAsync(sent, TestContext.Current.CancellationToken);
        await client.FlushAsync(TestContext.Current.CancellationToken);

        var received = new byte[sent.Length];
        await server.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task Connecting_to_an_address_nobody_is_listening_on_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var address = Path.Combine(Directory.CreateTempSubdirectory("rd-ctl-").FullName, "absent.sock");

        // The callers look for IOException to mean "no service is installed", so whatever the socket layer reports
        // has to arrive in that shape.
        await Assert.ThrowsAnyAsync<IOException>(() =>
            HostControlEndpoint.ConnectAsync(address, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_socket_left_behind_by_a_previous_run_does_not_stop_the_next_one()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var directory = Directory.CreateTempSubdirectory("rd-ctl-").FullName;
        var address = Path.Combine(directory, "host-control.sock");
        File.WriteAllText(address, "not really a socket");

        await using var listener = new HostControlSocketListener(address);
        await using var client = await HostControlEndpoint.ConnectAsync(address, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Taking_the_listener_down_removes_the_socket()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var address = Path.Combine(Directory.CreateTempSubdirectory("rd-ctl-").FullName, "host-control.sock");
        var listener = new HostControlSocketListener(address);
        Assert.True(File.Exists(address));

        await listener.DisposeAsync();
        Assert.False(File.Exists(address));
    }
}
