using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Transport.Quic;

/// <summary>
/// Mutual TLS between devices. Certificates are self-signed, so trust comes from pinning: each side accepts only the
/// fingerprint the rendezvous server forwarded for this connection attempt. Chain errors are therefore ignored.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class PeerTls
{
    public static readonly SslApplicationProtocol Alpn = new("rd/1");

    private const string TargetHost = "remotedesktop.peer";

    public static QuicClientConnectionOptions ClientOptions(DeviceIdentity identity, IPEndPoint remote, IPEndPoint? local, string expectedFingerprint) =>
        new()
        {
            RemoteEndPoint = remote,
            LocalEndPoint = local,
            DefaultStreamErrorCode = PeerErrorCodes.StreamAborted,
            DefaultCloseErrorCode = PeerErrorCodes.ConnectionClosed,
            IdleTimeout = TimeSpan.FromSeconds(30),
            // Keeps NAT and relay mappings alive while the session is quiet.
            KeepAliveInterval = TimeSpan.FromSeconds(10),
            MaxInboundBidirectionalStreams = 64,
            MaxInboundUnidirectionalStreams = 64,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [Alpn],
                TargetHost = TargetHost,
                ClientCertificates = [identity.Certificate],
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint),
            },
        };

    public static QuicServerConnectionOptions ServerOptions(DeviceIdentity identity, string expectedFingerprint) =>
        new()
        {
            DefaultStreamErrorCode = PeerErrorCodes.StreamAborted,
            DefaultCloseErrorCode = PeerErrorCodes.ConnectionClosed,
            IdleTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(10),
            MaxInboundBidirectionalStreams = 64,
            MaxInboundUnidirectionalStreams = 64,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [Alpn],
                ServerCertificate = identity.Certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint),
            },
        };

    private static bool Matches(X509Certificate? certificate, string expectedFingerprint) =>
        certificate is not null &&
        string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), expectedFingerprint, StringComparison.OrdinalIgnoreCase);
}

internal static class PeerErrorCodes
{
    public const long ConnectionClosed = 0;
    public const long StreamAborted = 1;
    public const long UnknownChannel = 2;
}
