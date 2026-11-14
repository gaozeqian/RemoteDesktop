namespace RemoteDesktop.Platform.Linux.Audio;

/// <summary>
/// The pump-until-ready loops both PulseAudio endpoints run after asking the server for a context or a stream.
/// </summary>
/// <remarks>
/// libpulse is asynchronous: the call that asks for a context or a stream returns at once and the answer arrives when
/// the main loop is iterated. The capturer and the player had this loop twice, identical except for the word in the
/// error message; the two copies are one here. A "what" names the stream so a log still says capture or playback.
/// </remarks>
internal static class PulseWait
{
    /// <summary>Ten seconds of one-millisecond polls; after that the server is not going to answer.</summary>
    private const int Attempts = 10_000;

    public static void ForContext(nint mainloop, nint context)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (Pulse.Iterate(mainloop, 0, out _) < 0)
            {
                throw new InvalidOperationException($"PulseAudio refused the connection: {Pulse.Describe(Pulse.ContextErrno(context))}.");
            }

            switch (Pulse.ContextGetState(context))
            {
                case Pulse.ContextReady:
                    return;
                case Pulse.ContextFailed or Pulse.ContextTerminated:
                    throw new InvalidOperationException($"PulseAudio refused the connection: {Pulse.Describe(Pulse.ContextErrno(context))}.");
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException("PulseAudio did not become ready.");
    }

    public static void ForStream(nint mainloop, nint context, nint stream, string what)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (Pulse.Iterate(mainloop, 0, out _) < 0)
            {
                throw new InvalidOperationException($"The {what} stream failed: {Pulse.Describe(Pulse.ContextErrno(context))}.");
            }

            switch (Pulse.StreamGetState(stream))
            {
                case Pulse.StreamReady:
                    return;
                case Pulse.StreamFailed or Pulse.StreamTerminated:
                    throw new InvalidOperationException($"The {what} stream failed: {Pulse.Describe(Pulse.ContextErrno(context))}.");
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException(
            $"The {what} stream did not become ready; its state stayed {Pulse.StreamGetState(stream)} ({Pulse.Describe(Pulse.ContextErrno(context))}).");
    }
}
