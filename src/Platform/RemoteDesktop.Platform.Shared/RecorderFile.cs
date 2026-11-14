namespace RemoteDesktop.Platform.Shared;

/// <summary>Removing the file a recording that never happened leaves behind.</summary>
/// <remarks>
/// Each recorder used to carry its own copy of this try/catch, including the same sentence about why an unplayable
/// file is worse than no file. Nothing in it is platform-specific, so it lives once; a delete that fails is still
/// swallowed, because throwing while closing a recording that never started helps nobody.
/// </remarks>
internal static class RecorderFile
{
    public static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving an unplayable file behind is better than throwing while closing one that never started.
        }
    }
}
