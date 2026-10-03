using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>Process-wide Media Foundation startup plus helpers shared by the encoder and decoder.</summary>
[SupportedOSPlatform("windows")]
internal static class MfRuntime
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFactory.MFStartup(useLightVersion: true).CheckError();
        return true;
    });

    public static void EnsureStarted() => _ = Started.Value;

    /// <summary>Enumerates transforms of a category for input → output subtypes.</summary>
    public static List<MftDescriptor> Enumerate(Guid category, uint flags, Guid inputSubtype, Guid outputSubtype)
    {
        EnsureStarted();
        var input = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = inputSubtype };
        var output = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = outputSubtype };

        var result = new List<MftDescriptor>();
        using var activates = MediaFactory.MFTEnumEx(category, flags, input, output);
        var index = 0;
        foreach (var activate in activates)
        {
            string name;
            try
            {
                name = activate.GetString(MfGuids.FriendlyName);
            }
            catch (SharpGenException)
            {
                name = $"MFT #{index}";
            }

            result.Add(new MftDescriptor(name, index));
            index++;
        }

        return result;
    }

    /// <summary>Activates the transform at <paramref name="index"/> of the same enumeration.</summary>
    public static (IMFTransform Transform, IMFActivate Activate) Activate(Guid category, uint flags, Guid inputSubtype, Guid outputSubtype, int index)
    {
        EnsureStarted();
        var input = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = inputSubtype };
        var output = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = outputSubtype };

        // Disposing the collection releases every element, so keep our own reference to the chosen one.
        using var activates = MediaFactory.MFTEnumEx(category, flags, input, output);
        IMFActivate? chosen = null;
        var i = 0;
        foreach (var activate in activates)
        {
            if (i++ == index)
            {
                chosen = activate.QueryInterface<IMFActivate>();
            }
        }

        if (chosen is null)
        {
            throw new InvalidOperationException("The transform is no longer registered.");
        }

        try
        {
            return (chosen.ActivateObject<IMFTransform>(), chosen);
        }
        catch
        {
            chosen.Dispose();
            throw;
        }
    }

    public static IMFMediaType CreateVideoType(Guid subtype, int width, int height, int frameRate)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MfGuids.MajorType, MfGuids.MediaTypeVideo).CheckError();
        type.Set(MfGuids.Subtype, subtype).CheckError();
        MediaFactory.MFSetAttributeSize(type, MfGuids.FrameSize, (uint)width, (uint)height).CheckError();
        if (frameRate > 0)
        {
            MediaFactory.MFSetAttributeRatio(type, MfGuids.FrameRate, (uint)frameRate, 1).CheckError();
        }

        MediaFactory.MFSetAttributeRatio(type, MfGuids.PixelAspectRatio, 1, 1).CheckError();
        type.Set(MfGuids.InterlaceMode, MfGuids.InterlaceProgressive).CheckError();
        return type;
    }

    /// <summary>Wraps bytes in a single-buffer sample.</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        try
        {
            CopyInto(buffer, data);
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public static IMFSample CreateEmptySample(int capacity)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(capacity);
        try
        {
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public static unsafe void CopyInto(IMFMediaBuffer buffer, ReadOnlySpan<byte> data)
    {
        buffer.Lock(out var pointer, out var maxLength, out _);
        try
        {
            if (maxLength < data.Length)
            {
                throw new InvalidOperationException("Media buffer too small.");
            }

            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = data.Length;
    }

    /// <summary>Copies the sample's payload into a new array.</summary>
    public static unsafe byte[] ToArray(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            return new ReadOnlySpan<byte>((void*)pointer, length).ToArray();
        }
        finally
        {
            buffer.Unlock();
        }
    }
}

internal sealed record MftDescriptor(string Name, int Index);
