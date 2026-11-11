using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// <c>Transport:RendezvousCertificatePath</c> through the product's own <see cref="RendezvousClient"/>: a
/// <c>wss://</c> connection accepts exactly the one pinned certificate, and without a pin the platform's validation
/// decides - which refuses a self-signed certificate. The signaling server is the real one, serving TLS.
/// </summary>
public sealed class RendezvousCertificatePinTests : IDisposable
{
    private const string Password = "test-only";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rd-pin-{Guid.NewGuid():N}");

    public RendezvousCertificatePinTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned by the OS later.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_pinned_certificate_is_accepted_whether_the_file_is_der_or_pem(bool pem)
    {
        using var served = SelfSigned();
        var pinPath = WritePublicHalf(served, pem ? "pinned.pem" : "pinned.cer", pem);
        await using var server = await StartServerAsync(served);
        var log = new CapturingLogger();
        using var identity = DeviceIdentity.CreateEphemeral();
        using var client = NewClient(server, pinPath, identity, log);

        await using var run = Run(client);

        await run.OnlineAsync;
        Assert.NotNull(client.DeviceId);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task A_different_self_signed_certificate_is_refused_even_though_it_is_pinned_for_someone_else()
    {
        using var served = SelfSigned();
        using var other = SelfSigned();
        var pinPath = WritePublicHalf(other, "other.cer", pem: false);
        await using var server = await StartServerAsync(served);
        var log = new CapturingLogger();
        using var identity = DeviceIdentity.CreateEphemeral();
        using var client = NewClient(server, pinPath, identity, log);

        await using var run = Run(client);

        await log.FirstWarningAsync();
        Assert.NotEqual(RendezvousState.Online, client.State);
        Assert.Null(client.DeviceId);
    }

    [Fact]
    public async Task Without_a_pin_a_self_signed_certificate_falls_to_the_platforms_validation_and_is_refused()
    {
        using var served = SelfSigned();
        await using var server = await StartServerAsync(served);
        var log = new CapturingLogger();
        using var identity = DeviceIdentity.CreateEphemeral();
        using var client = NewClient(server, pinPath: null, identity, log);

        await using var run = Run(client);

        await log.FirstWarningAsync();
        Assert.NotEqual(RendezvousState.Online, client.State);
        Assert.Null(client.DeviceId);
    }

    private static RendezvousClient NewClient(
        Microsoft.AspNetCore.Builder.WebApplication server, string? pinPath, DeviceIdentity identity, ILogger<RendezvousClient> log) =>
        new(
            new TransportOptions
            {
                RendezvousUri = TestRendezvous.SignalingUri(server, scheme: "wss"),
                RendezvousCertificatePath = pinPath,
            },
            identity,
            log);

    private async Task<Microsoft.AspNetCore.Builder.WebApplication> StartServerAsync(X509Certificate2 served)
    {
        var path = Path.Combine(_directory, $"server-{Guid.NewGuid():N}.pfx");
        await File.WriteAllBytesAsync(path, served.Export(X509ContentType.Pfx, Password), Ct);
        return await TestRendezvous.StartAsync(
            ("Urls", "https://127.0.0.1:0"),
            ("Kestrel:Certificates:Default:Path", path),
            ("Kestrel:Certificates:Default:Password", Password));
    }

    private string WritePublicHalf(X509Certificate2 certificate, string name, bool pem)
    {
        var path = Path.Combine(_directory, name);
        if (pem)
        {
            File.WriteAllText(path, certificate.ExportCertificatePem());
        }
        else
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        }

        return path;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Windows' TLS stack needs a key it can find again, which a PFX round trip provides.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, Password), Password, X509KeyStorageFlags.Exportable);
    }

    private static RunningClient Run(RendezvousClient client) => new(client);

    /// <summary>Runs the client's connect loop until disposed; completes <see cref="OnlineAsync"/> once it is online.</summary>
    private sealed class RunningClient : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public RunningClient(RendezvousClient client)
        {
            var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += (_, _) =>
            {
                if (client.State == RendezvousState.Online)
                {
                    online.TrySetResult();
                }
            };

            OnlineAsync = online.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            _run = client.RunAsync(_stop.Token);
        }

        public Task OnlineAsync { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();

            // The run loop ends by this cancellation, and the bound is waited on with no cancellation so that the
            // test's own token (which may be the one that just fired) cannot turn teardown into an exception of its own.
            await _run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            _stop.Dispose();
        }
    }

    /// <summary>Remembers the warnings, which is where the client says why a connection did not come up.</summary>
    private sealed class CapturingLogger : ILogger<RendezvousClient>
    {
        private readonly List<string> _warnings = [];
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public Task FirstWarningAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }

            _first.TrySetResult();
        }
    }
}
