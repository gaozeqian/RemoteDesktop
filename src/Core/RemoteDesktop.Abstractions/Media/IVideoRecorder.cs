namespace RemoteDesktop.Abstractions.Media;

/// <summary>Why a recording stopped making progress. The frames already written stay valid.</summary>
public enum VideoRecorderFailure
{
    /// <summary>The recording is running normally.</summary>
    None,

    /// <summary>The picture changed size, which the encoder's fixed input type cannot follow.</summary>
    PictureSizeChanged,

    /// <summary>The encoder or the disk refused a frame (no space, codec error).</summary>
    WriteFailed,

    /// <summary>Nothing was ever handed over, so the recording holds no picture and no file was kept.</summary>
    NothingRecorded,
}

/// <summary>Records video frames into an MP4 container file.</summary>
public interface IVideoRecorder : IAsyncDisposable
{
    /// <summary>Whether a recording session is currently active.</summary>
    bool IsRecording { get; }

    /// <summary>Full path to the current or last recording file.</summary>
    string? OutputFilePath { get; }

    /// <summary>Duration elapsed since the recording started.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// How many frames the encoder has taken so far. A caller can watch it to tell a recording that is progressing
    /// from one whose clock is running over a file that stopped growing.
    /// </summary>
    long WrittenFrames { get; }

    /// <summary>
    /// Why nothing more will be written, or <see cref="VideoRecorderFailure.None"/> while frames are still going
    /// through. Never resets during a recording: the first reason is the one that explains where the file ends.
    /// </summary>
    VideoRecorderFailure Failure { get; }

    /// <summary>Starts recording frames to the specified file.</summary>
    void Start(string filePath, int width, int height, int frameRate = 30, int bitrateKbps = 4000);

    /// <summary>
    /// Feeds one BGRA32 frame into the recorder, top row first. Frames are timed by the recorder itself, so the file's
    /// timeline is its own and callers do not have to produce monotonic timestamps.
    /// </summary>
    void RecordFrame(ReadOnlySpan<byte> bgraPixels, int stride, int width, int height);

    /// <summary>
    /// Stops the recording, flushes data to disk, and returns the output file path. Throws
    /// <see cref="InvalidOperationException"/> when no frame ever reached the encoder: there is nothing to finalize,
    /// and the file that would have held it is removed rather than left behind unplayable. When no recording is
    /// running (none was started, or it was already stopped) nothing is stopped and nothing throws: the result is the
    /// path of the latest recording, or an empty string when there has not been one.
    /// </summary>
    Task<string> StopAsync();
}

public interface IVideoRecorderFactory
{
    IVideoRecorder Create();
}
