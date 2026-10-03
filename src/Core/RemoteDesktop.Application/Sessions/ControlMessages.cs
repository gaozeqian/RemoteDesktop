using RemoteDesktop.Domain;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

/// <summary>Mapping and I/O helpers for the control channel.</summary>
internal static class ControlMessages
{
    /// <summary>Control messages are small; anything larger is a protocol violation.</summary>
    private const int MaxSize = 64 * 1024;

    public static SessionModeMessage ToMessage(SessionMode mode) =>
        mode == SessionMode.View ? SessionModeMessage.SessionModeView : SessionModeMessage.SessionModeControl;

    public static SessionMode FromMessage(SessionModeMessage mode) =>
        mode == SessionModeMessage.SessionModeView ? SessionMode.View : SessionMode.Control;

    public static Permissions FromWire(uint permissions) => (Permissions)permissions & Permissions.All;

    public static ValueTask WriteAsync(IChannel channel, ControlMessage message, CancellationToken cancellationToken) =>
        ProtobufFrames.WriteAsync(channel.Stream, message, cancellationToken);

    public static ValueTask<ControlMessage?> ReadAsync(IChannel channel, CancellationToken cancellationToken) =>
        ProtobufFrames.ReadAsync(channel.Stream, ControlMessage.Parser, MaxSize, cancellationToken);

    /// <exception cref="SessionException">The peer closed the channel or sent something else.</exception>
    public static async ValueTask<ControlMessage> ExpectAsync(IChannel channel, ControlMessage.BodyOneofCase expected, CancellationToken cancellationToken)
    {
        var message = await ReadAsync(channel, cancellationToken).ConfigureAwait(false)
            ?? throw new SessionException(SessionFailure.ProtocolError, "The remote device closed the session.");

        // An AuthResult may arrive instead of the expected message when the host refuses early.
        if (message.BodyCase != expected && message.BodyCase == ControlMessage.BodyOneofCase.AuthResult && !message.AuthResult.Accepted)
        {
            throw SessionException.FromAuthFailure(message.AuthResult.Failure);
        }

        return message.BodyCase == expected
            ? message
            : throw new SessionException(SessionFailure.ProtocolError, $"Expected {expected}, received {message.BodyCase}.");
    }
}
