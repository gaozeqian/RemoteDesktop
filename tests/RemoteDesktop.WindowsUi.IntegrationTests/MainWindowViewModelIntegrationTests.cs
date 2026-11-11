using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;
using RemoteDesktop.Client;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Domain;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

/// <summary>
/// Integration tests verifying the client's main ViewModel behaviours,
/// including startup deep-link activation, fixed password form validation,
/// connection mode toggling, and recent device persistence.
/// </summary>
public sealed class MainWindowViewModelIntegrationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), "rd-ui-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDataDir))
            {
                Directory.Delete(_tempDataDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void Startup_with_connection_link_populates_target_device_id_without_auto_connecting()
    {
        Assert.True(ConnectionLink.TryParse("remotedesktop://connect?code=902226105", out var link));

        string[] args = ["--DataDirectory", _tempDataDir, "--HostMode", "standalone"];
        using var host = ClientHost.Build(args, link);
        var viewModel = (MainWindowViewModel)host.Services.GetService(typeof(MainWindowViewModel))!;

        Assert.NotNull(viewModel);
        Assert.Equal("902 226 105", viewModel.RemoteDeviceId);
        Assert.False(viewModel.IsConnected, "Deep-link must NOT auto-connect; operator must confirm.");
    }

    [Fact]
    public void Link_activation_during_runtime_updates_target_code()
    {
        string[] args = ["--DataDirectory", _tempDataDir, "--HostMode", "standalone"];
        using var host = ClientHost.Build(args);
        var viewModel = (MainWindowViewModel)host.Services.GetService(typeof(MainWindowViewModel))!;

        Assert.NotNull(viewModel);

        Assert.True(ConnectionLink.TryParse("remotedesktop://888777666", out var dynamicLink));
        viewModel.ApplyConnectionLink(dynamicLink);

        Assert.Equal("888 777 666", viewModel.RemoteDeviceId);
    }

    [Fact]
    public void Fixed_password_validation_rejects_a_password_one_character_short_of_the_minimum()
    {
        var error = MainWindowViewModel.ValidateFixedPassword("abcdefg");
        Assert.Equal("固定密码长度至少需要 8 个字符。", error);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("  ValidP@sswordLongEnough  ")]
    public void Fixed_password_validation_accepts_valid_length_passwords(string input)
    {
        var error = MainWindowViewModel.ValidateFixedPassword(input);
        Assert.Null(error);
    }

    [Fact]
    public void Connection_modes_contain_all_standard_features_and_support_selection()
    {
        string[] args = ["--DataDirectory", _tempDataDir, "--HostMode", "standalone"];
        using var host = ClientHost.Build(args);
        var viewModel = (MainWindowViewModel)host.Services.GetService(typeof(MainWindowViewModel))!;

        Assert.NotNull(viewModel);
        Assert.NotEmpty(viewModel.ConnectionModes);

        var features = viewModel.ConnectionModes.Select(m => m.Feature).ToList();
        Assert.Contains(StandaloneFeature.Control, features);
        Assert.Contains(StandaloneFeature.Watch, features);
        Assert.Contains(StandaloneFeature.FileTransfer, features);
        Assert.Contains(StandaloneFeature.Terminal, features);
        Assert.Contains(StandaloneFeature.Camera, features);

        var fileTransferMode = viewModel.ConnectionModes.First(m => m.Feature == StandaloneFeature.FileTransfer);
        viewModel.SelectedConnectionMode = fileTransferMode;
        Assert.Equal(StandaloneFeature.FileTransfer, viewModel.SelectedConnectionMode.Feature);
    }

    [Fact]
    public async Task Recent_devices_workflow_records_alias_and_password()
    {
        string[] args = ["--DataDirectory", _tempDataDir, "--HostMode", "standalone"];
        using var host = ClientHost.Build(args);
        var store = (IRecentDevicesStore)host.Services.GetService(typeof(IRecentDevicesStore))!;

        Assert.NotNull(store);

        const string deviceCode = "123 456 789";
        const string alias = "Workstation-A";
        const string password = "TestPassword123!";

        await MainWindowViewModel.RecordConnectedDeviceAsync(
            store,
            deviceCode,
            alias,
            password,
            rememberPassword: true,
            Ct);

        var recent = await store.GetAllAsync(Ct);
        var item = Assert.Single(recent);
        Assert.Equal(deviceCode, item.DeviceId);
        Assert.Equal(alias, item.Alias);

        var remembered = await store.GetPasswordAsync(deviceCode, Ct);
        Assert.Equal(password, remembered);
    }
}
