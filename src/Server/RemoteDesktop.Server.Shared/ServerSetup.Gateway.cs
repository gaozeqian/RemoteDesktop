using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Server.Shared;

public static partial class ServerSetup
{
    /// <summary>
    /// Configures the public HTTPS console listener and its loopback-only upstream. The certificate is shared with
    /// signaling when that also uses the generated machine certificate, so there is only one public half to distribute.
    /// </summary>
    /// <returns><c>true</c> when the console's trusted-proxy list changed.</returns>
    private static bool ConfigureGateway(
        JsonObject settings,
        JsonObject ops,
        ServerSetupOptions options,
        string configDirectory,
        string[] additionalNames,
        List<string> notes)
    {
        var section = Section(settings, "Gateway");
        var listen = Given(options.GatewayListen) ?? Text(section, "Listen") ?? DefaultGatewayListen;
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(address.Host)
            || address.AbsolutePath != "/"
            || address.Query.Length > 0
            || address.Fragment.Length > 0
            || address.UserInfo.Length > 0)
        {
            throw new InvalidOperationException($"The Ops gateway address must be an HTTPS URL without a path (got '{listen}').");
        }

        // Where the console really listens, as a loopback address: ConfigureOps has already applied --ops-listen to
        // `ops`, and a value from an earlier run is still in it. The default stays 5090 when nothing says otherwise.
        var upstream = LocalOpsEventsEndpoint(ops);

        section["Listen"] = listen;

        // The proxy is not a general-purpose forwarder: its one route and one loopback destination are rewritten on
        // every setup run. X-Forwarded=Set discards client-supplied forwarding headers before adding the real ones.
        var proxy = Section(settings, "ReverseProxy");
        var routes = Section(proxy, "Routes");
        var route = Section(routes, "ops");
        route["ClusterId"] = "ops";
        route["Match"] = new JsonObject { ["Path"] = "/{**catch-all}" };
        route["Transforms"] = new JsonArray(new JsonObject { ["X-Forwarded"] = "Set" });

        var clusters = Section(proxy, "Clusters");
        var cluster = Section(clusters, "ops");
        var destinations = Section(cluster, "Destinations");
        var loopback = Section(destinations, "loopback");
        loopback["Address"] = upstream;

        var certificates = Section(Section(Section(settings, "Kestrel"), "Certificates"), "Default");
        foreach (var stale in (string[])["Path", "KeyPath", "Password", "Subject", "Store", "Location", "AllowInvalid"])
        {
            certificates.Remove(stale);
        }

        var certificateNames = additionalNames;
        if (!IPAddress.TryParse(address.Host, out var listenAddress) || !IPAddress.Any.Equals(listenAddress) && !IPAddress.IPv6Any.Equals(listenAddress))
        {
            certificateNames = [.. certificateNames, address.Host];
        }

        string publicCertificate;
        if (OperatingSystem.IsWindows())
        {
            SelfSignedCertificate.Ensure(configDirectory, notes, certificateNames);
            certificates["Subject"] = SelfSignedCertificate.NameForThisMachine;
            certificates["Store"] = SelfSignedCertificate.CertificateStoreName;
            certificates["Location"] = SelfSignedCertificate.CertificateStoreLocation;
            certificates["AllowInvalid"] = "true";
            publicCertificate = Path.Combine(configDirectory, SelfSignedCertificate.PublicFolder, "signaling.cer");
        }
        else
        {
            var (certificatePath, keyPath) = SelfSignedCertificate.EnsurePem(
                Path.Combine(configDirectory, SelfSignedCertificate.PublicFolder),
                notes,
                certificateNames);
            certificates["Path"] = certificatePath;
            certificates["KeyPath"] = keyPath;
            publicCertificate = certificatePath;
        }

        var opsSection = Section(ops, "Ops");
        var trusted = opsSection["TrustedProxies"] as JsonArray;
        if (trusted is null)
        {
            trusted = [];
            opsSection["TrustedProxies"] = trusted;
        }

        var changed = !trusted.Any(entry => string.Equals(entry?.GetValue<string>(), IPAddress.Loopback.ToString(), StringComparison.Ordinal));
        if (changed)
        {
            trusted.Add(IPAddress.Loopback.ToString());
        }

        notes.Add($"The Ops console is available through the HTTPS gateway on {listen}; its upstream is {upstream}.");
        notes.Add($"Ops gateway clients must trust {publicCertificate}; the certificate contains this machine's IPv4 addresses as IP SAN entries.");
        return changed;
    }

}
