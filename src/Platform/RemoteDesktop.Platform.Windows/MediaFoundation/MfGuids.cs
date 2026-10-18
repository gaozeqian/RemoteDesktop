namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>Media Foundation constants from mfapi.h / codecapi.h, spelled out to keep the wrapper self-explanatory.</summary>
internal static class MfGuids
{
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");

    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    /// <summary>MF_LOW_LATENCY, which is also CODECAPI_AVLowLatencyMode.</summary>
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public static readonly Guid CodecApiRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CodecApiMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CodecApiGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CodecApiBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CodecApiForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint EnumSyncMft = 0x1;
    public const uint EnumAsyncMft = 0x2;
    public const uint EnumHardware = 0x4;
    public const uint EnumSortAndFilter = 0x40;

    public const uint InterlaceProgressive = 2;
    public const uint H264ProfileMain = 77;
    public const uint RateControlCbr = 0;

    public const int OutputStreamProvidesSamples = 0x100;

    public const int EventTransformNeedInput = 601;
    public const int EventTransformHaveOutput = 602;

    public const int ErrorNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int ErrorStreamChange = unchecked((int)0xC00D6D61);
    public const int ErrorNotAccepting = unchecked((int)0xC00D36B5);
}
