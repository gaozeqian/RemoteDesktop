using RemoteDesktop.Domain;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Issued by the rendezvous server when hole punching fails; both peers present the token to the relay, which
/// pairs them and forwards their (end-to-end encrypted) UDP packets until <see cref="ExpiresAt"/>.
/// </summary>
public sealed record RelayTicket(string Token, DeviceId Controller, DeviceId Host, DateTimeOffset ExpiresAt);
