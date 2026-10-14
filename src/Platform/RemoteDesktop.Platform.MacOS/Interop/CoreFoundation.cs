using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>
/// The Apple framework and library names the platform layer binds to. Kept in one place so a typo shows up as one
/// edit rather than as a mysterious load failure at runtime.
/// </summary>
internal static class NativeLibraries
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    public const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    public const string VideoToolbox = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    public const string Security = "/System/Library/Frameworks/Security.framework/Security";
    public const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    /// <summary>
    /// The Objective-C helper built from <c>native/macos</c>. It ships inside the .app bundle, so the loader is
    /// pointed at it through <see cref="MacNativeLibraryResolver"/> rather than by an absolute build path.
    /// </summary>
    public const string MacNative = "RemoteDesktopMacNative";
}

/// <summary>
/// CoreFoundation interop: the reference-counted C layer every other Apple framework is expressed in.
/// </summary>
/// <remarks>
/// Every function here takes and returns raw <see cref="nint"/> handles on purpose. Apple's frameworks hand back
/// <c>+1</c> (owned) references and expect them released with <see cref="CFRelease"/>; wrapping them in managed types
/// would hide that contract, and the whole platform layer is the one place that should be forced to think about it.
/// </remarks>
internal static unsafe partial class CoreFoundation
{
    public const int Utf8Encoding = 0x0800_0100;

    /// <summary>Passed where an allocator is expected to mean "the default one".</summary>
    public static readonly nint DefaultAllocator = 0;

    /// <summary>Passed where a callback-struct pointer is expected to mean "the default one".</summary>
    public static readonly nint DefaultCallbacks = 0;

    private static readonly nint CoreFoundationHandle = NativeLibrary.Load(NativeLibraries.CoreFoundation);

    // kCFBooleanTrue / kCFBooleanFalse are global *variables* holding the object pointer, not functions, so the
    // symbol has to be looked up and then dereferenced once.
    private static readonly nint BooleanTrue = *(nint*)NativeLibrary.GetExport(CoreFoundationHandle, "kCFBooleanTrue");
    private static readonly nint BooleanFalse = *(nint*)NativeLibrary.GetExport(CoreFoundationHandle, "kCFBooleanFalse");

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial void CFRelease(nint cf);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFRetain(nint cf);

    [LibraryImport(NativeLibraries.CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint CFStringCreateWithCString(nint allocator, string cStr, int encoding);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFStringGetLength(nint theString);

    /// <summary>Copies the string into <paramref name="buffer"/>. Returns non-zero on success.</summary>
    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial byte CFStringGetCString(nint theString, byte* buffer, nint bufferSize, int encoding);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFNumberCreate(nint allocator, int theType, int* value);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial byte CFNumberGetValue(nint number, int theType, int* value);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFArrayCreate(nint allocator, nint* values, nint numValues, nint callbacks);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFArrayGetCount(nint theArray);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFArrayGetValueAtIndex(nint theArray, nint idx);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFDictionaryCreate(
        nint allocator, nint* keys, nint* values, nint numValues, nint keyCallbacks, nint valueCallbacks);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFDictionaryGetValue(nint theDict, nint key);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial nint CFDataGetLength(nint theData);

    [LibraryImport(NativeLibraries.CoreFoundation)]
    internal static partial byte* CFDataGetBytePtr(nint theData);

    /// <summary>CFNumber type codes. <c>SInt32Type</c> is what the frameworks expect for integer properties.</summary>
    public const int NumberSInt32Type = 3;

    public static nint Boolean(bool value) => value ? BooleanTrue : BooleanFalse;

    /// <summary>Wraps a managed string as a +1 CFString the caller must release.</summary>
    public static nint CreateString(string value) => CFStringCreateWithCString(DefaultAllocator, value, Utf8Encoding);

    /// <summary>Reads a CFString back into a managed string, or <c>null</c> when the handle is not one.</summary>
    public static string? GetString(nint cfString)
    {
        if (cfString == 0)
        {
            return null;
        }

        var length = (int)CFStringGetLength(cfString);
        // Worst case for UTF-8 is 3 bytes per UTF-16 code unit, plus the terminator.
        var size = (length * 3) + 1;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (CFStringGetCString(cfString, p, size, Utf8Encoding) == 0)
            {
                return null;
            }
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>Wraps an int as a +1 CFNumber the caller must release.</summary>
    public static nint CreateNumber(int value)
    {
        var local = value;
        return CFNumberCreate(DefaultAllocator, NumberSInt32Type, &local);
    }
}
