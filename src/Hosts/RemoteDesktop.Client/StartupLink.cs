using RemoteDesktop.Application.Links;

namespace RemoteDesktop.Client;

/// <summary>
/// The link this process was launched with, if any. Registered in the container even when there is none, so the main
/// window can always ask the question and the answer has one shape.
/// </summary>
/// <param name="Value">The link, or <c>null</c> for an ordinary launch.</param>
public sealed record StartupLink(ConnectionLink? Value);
