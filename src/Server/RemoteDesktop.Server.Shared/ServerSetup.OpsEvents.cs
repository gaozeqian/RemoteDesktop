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
    /// Gives all three server processes the same event-channel settings. Existing keys are authoritative: an upgrade
    /// can complete a missing side, but it must never rotate a secret that another node already uses.
    /// </summary>
    private static bool ConfigureOpsEvents(
        JsonObject rendezvous,
        JsonObject relay,
        JsonObject ops,
        ServerSetupOptions options,
        List<string> notes)
    {
        var nodes = new[] { Section(rendezvous, "OpsEvents"), Section(relay, "OpsEvents") };
        var configuredEndpoint = Expand(options.OpsEventsEndpoint);
        var explicitEndpoint = IsAuto(configuredEndpoint) ? null : configuredEndpoint;
        if (explicitEndpoint is not null)
        {
            ValidateOpsEventsEndpoint(explicitEndpoint);
        }

        var existingEndpoints = nodes
            .Select(node => Text(node, "Endpoint"))
            .Where(endpoint => endpoint is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var existing in existingEndpoints)
        {
            ValidateOpsEventsEndpoint(existing);
        }

        var repaired = false;
        for (var index = 0; index < nodes.Length; index++)
        {
            if (explicitEndpoint is not null)
            {
                nodes[index]["Endpoint"] = explicitEndpoint;
            }
            else if (Text(nodes[index], "Endpoint") is null)
            {
                nodes[index]["Endpoint"] = existingEndpoints.Length == 1
                    ? existingEndpoints[0]
                    : LocalOpsEventsEndpoint(ops);
                repaired = true;
            }
        }

        var keys = Section(Section(Section(ops, "Ops"), "Ingest"), "Keys");
        var configuredKey = Expand(options.OpsIngestKey);
        if (!IsAuto(configuredKey))
        {
            ValidateOpsKey(configuredKey);
            var changed = !string.Equals(Text(keys, DefaultOpsKeyId), configuredKey, StringComparison.Ordinal);
            foreach (var node in nodes)
            {
                node["KeyId"] = DefaultOpsKeyId;
                node["Key"] = configuredKey;
            }

            keys[DefaultOpsKeyId] = configuredKey;
            notes.Add("The Ops event channel was configured with the key supplied by the installer.");
            return changed;
        }

        var acceptedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in keys)
        {
            var value = property.Value?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Ops ingest key '{property.Key}' is empty; fix or remove it before running setup.");
            }

            ValidateOpsKey(value);
            acceptedKeys.Add(property.Key, value);
        }

        // The union is what a node may reuse. A duplicated id with different secrets can never authenticate both
        // nodes against the same console, so guessing which side to overwrite would only hide a broken deployment.
        var knownKeys = new Dictionary<string, string>(acceptedKeys, StringComparer.Ordinal);
        var nodeKeys = new OpsEventKey?[nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            if (Text(nodes[index], "Key") is not { } value)
            {
                continue;
            }

            ValidateOpsKey(value);
            var pair = new OpsEventKey(Text(nodes[index], "KeyId") ?? DefaultOpsKeyId, value);
            nodeKeys[index] = pair;
            AddKnownOpsKey(knownKeys, pair);
        }

        var opsChanged = false;
        var generated = false;
        for (var index = 0; index < nodes.Length; index++)
        {
            if (nodeKeys[index] is not null)
            {
                continue;
            }

            OpsEventKey pair;
            var requestedId = Text(nodes[index], "KeyId");
            if (requestedId is not null && knownKeys.TryGetValue(requestedId, out var requestedKey))
            {
                pair = new OpsEventKey(requestedId, requestedKey);
            }
            else if (nodeKeys[1 - index] is { } other)
            {
                pair = other;
            }
            else if (acceptedKeys.Count == 1)
            {
                var only = acceptedKeys.Single();
                pair = new OpsEventKey(only.Key, only.Value);
            }
            else if (knownKeys.Count == 0)
            {
                pair = new OpsEventKey(DefaultOpsKeyId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinimumKeyBytes)));
                knownKeys.Add(pair.Id, pair.Value);
                generated = true;
            }
            else
            {
                throw new InvalidOperationException(
                    "The Ops event settings are incomplete and contain multiple possible ingest keys. " +
                    "Set --ops-ingest-key explicitly so setup cannot choose the wrong secret.");
            }

            nodes[index]["KeyId"] = pair.Id;
            nodes[index]["Key"] = pair.Value;
            nodeKeys[index] = pair;
            repaired = true;
        }

        // Extra console keys are retained for other nodes. Every key used here must also be accepted by the console.
        foreach (var pair in nodeKeys.Select(pair => pair!.Value))
        {
            if (acceptedKeys.ContainsKey(pair.Id))
            {
                continue;
            }

            keys[pair.Id] = pair.Value;
            acceptedKeys.Add(pair.Id, pair.Value);
            opsChanged = true;
            repaired = true;
        }

        if (generated)
        {
            notes.Add("An Ops ingest key was generated and shared by Rendezvous, Relay and the console.");
        }
        else if (repaired || explicitEndpoint is not null)
        {
            notes.Add("The Ops event channel was completed without rotating an existing ingest key.");
        }

        return opsChanged;
    }

    private static void AddKnownOpsKey(Dictionary<string, string> keys, OpsEventKey pair)
    {
        if (keys.TryGetValue(pair.Id, out var existing) && !string.Equals(existing, pair.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Ops ingest key id '{pair.Id}' has different values in the server settings. " +
                "Set --ops-ingest-key explicitly to synchronize the deployment.");
        }

        keys[pair.Id] = pair.Value;
    }

    private static string LocalOpsEventsEndpoint(JsonObject ops)
    {
        var listen = Text(ops["Ops"] as JsonObject, "Listen");
        if (listen is not null
            && Uri.TryCreate(listen, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            // Uri keeps the brackets on an IPv6 host, so the wildcard spelling is "[::]".
            var host = uri.Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : uri.Host;
            return new UriBuilder(uri.Scheme, host, uri.Port).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return DefaultOpsEventsEndpoint;
    }

    private static void ValidateOpsEventsEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"The Ops events endpoint must be an absolute http(s) URL without a query or fragment (got '{endpoint}').");
        }
    }

    private readonly record struct OpsEventKey(string Id, string Value);

    private static void ValidateOpsKey(string key) =>
        Base64Key.Decode(
            key,
            "The Ops ingest key must be base64 (generate one with: openssl rand -base64 32).",
            length => $"The Ops ingest key must be at least {MinimumKeyBytes} bytes ({length} given). Generate one with: openssl rand -base64 32.",
            MinimumKeyBytes);

}
