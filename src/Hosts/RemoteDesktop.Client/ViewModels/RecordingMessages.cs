using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>
/// How the recording windows word a recorder that stopped making progress. The recorder reports only why; the
/// wording lives here because the client is what speaks to the user, and both windows must say the same thing.
/// </summary>
internal static class RecordingMessages
{
    /// <summary>Why the recording ended early, or null while it is still going.</summary>
    public static string? Interrupted(VideoRecorderFailure failure) => failure switch
    {
        VideoRecorderFailure.PictureSizeChanged => "录制已中断：画面尺寸发生变化，本次录制到此为止。",
        VideoRecorderFailure.WriteFailed => "录制已中断：写入视频失败（磁盘空间不足或编码器出错）。",
        VideoRecorderFailure.NothingRecorded => "录制已中断：没有收到任何画面，没有生成文件。",
        _ => null,
    };

    /// <summary>
    /// What to say when stopping the recording itself failed. The reason is included: this is where a vague sentence
    /// costs the most, because the recording is the only thing the user was waiting for.
    /// </summary>
    public static string StopFailed(VideoRecorderFailure failure, Exception exception) => failure switch
    {
        VideoRecorderFailure.NothingRecorded =>
            "本次录制没有收到任何画面（对方的画面一直没有变化），所以没有生成文件。",
        _ => $"停止录制失败：{exception.Message}",
    };
}
