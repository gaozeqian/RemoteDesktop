using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;

namespace RemoteDesktop.Core.UnitTests;

public sealed class LocalHostControlTests
{
    [Fact]
    public async Task A_failed_password_save_does_not_change_the_runtime_credential()
    {
        var credentials = new HostCredentials { FixedPassword = "old-password" };
        var control = new LocalHostControl(null!, credentials, null!, null, false, new FailingSecretStore());

        await Assert.ThrowsAsync<IOException>(() => control.SetFixedPasswordAsync("new-password", CancellationToken.None));
        Assert.Equal("old-password", credentials.FixedPassword);
    }

    [Fact]
    public async Task A_failed_password_delete_keeps_the_runtime_credential()
    {
        var credentials = new HostCredentials { FixedPassword = "old-password" };
        var control = new LocalHostControl(null!, credentials, null!, null, false, new FailingSecretStore());

        await Assert.ThrowsAsync<IOException>(() => control.SetFixedPasswordAsync(null, CancellationToken.None));
        Assert.Equal("old-password", credentials.FixedPassword);
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public void Save(string name, ReadOnlySpan<byte> secret) => throw new IOException("injected");

        public byte[]? Load(string name) => null;

        public void Delete(string name) => throw new IOException("injected");
    }
}
