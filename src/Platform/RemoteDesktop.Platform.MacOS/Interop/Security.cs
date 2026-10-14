using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>
/// Keychain Services interop, used to persist the device identity and the fixed password.
/// </summary>
/// <remarks>
/// The <c>kSec*</c> names are CoreFoundation string constants exported as global variables, so they are resolved once
/// through <see cref="NativeLibrary.GetExport"/> and dereferenced — there is no function to bind to.
/// </remarks>
internal static unsafe partial class Security
{
    public const int ErrSecSuccess = 0;
    public const int ErrSecItemNotFound = -25300;
    public const int ErrSecDuplicateItem = -25299;
    public const int ErrSecAuthFailed = -25293;

    private static readonly nint Handle = NativeLibrary.Load(NativeLibraries.Security);

    private static nint Constant(string name) => *(nint*)NativeLibrary.GetExport(Handle, name);

    public static nint SecClass => Constant("kSecClass");
    public static nint SecClassGenericPassword => Constant("kSecClassGenericPassword");
    public static nint SecAttrService => Constant("kSecAttrService");
    public static nint SecAttrAccount => Constant("kSecAttrAccount");
    public static nint SecAttrLabel => Constant("kSecAttrLabel");
    public static nint SecValueData => Constant("kSecValueData");
    public static nint SecReturnData => Constant("kSecReturnData");
    public static nint SecMatchLimit => Constant("kSecMatchLimit");
    public static nint SecMatchLimitOne => Constant("kSecMatchLimitOne");
    public static nint SecAttrAccessible => Constant("kSecAttrAccessible");
    public static nint SecAttrAccessibleAfterFirstUnlock => Constant("kSecAttrAccessibleAfterFirstUnlock");

    [LibraryImport(NativeLibraries.Security)]
    internal static partial int SecItemAdd(nint attributes, nint* result);

    [LibraryImport(NativeLibraries.Security)]
    internal static partial int SecItemCopyMatching(nint query, nint* result);

    [LibraryImport(NativeLibraries.Security)]
    internal static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(NativeLibraries.Security)]
    internal static partial int SecItemDelete(nint query);
}

/// <summary>
/// IOKit interop for the power assertion that keeps the machine awake while somebody is connected.
/// </summary>
internal static unsafe partial class IOKit
{
    /// <summary>The assertion level that means "on" (<c>kIOPMAssertionLevelOn</c>).</summary>
    public const int AssertionLevelOn = 255;

    /// <summary>
    /// The assertion type that stops the system idling to sleep, spelled as the literal the SDK defines the constant
    /// to be.
    /// </summary>
    /// <remarks>
    /// Deliberately a string, not a symbol lookup — the <c>kSec*</c> pattern does not work here. In the SDK,
    /// <c>kIOPMAssertionTypePreventUserIdleSystemSleep</c> and <c>kIOPMAssertionLevelOn</c> are <c>#define</c>s over
    /// <c>CFSTR("PreventUserIdleSystemSleep")</c> and <c>255</c>, so IOKit exports no symbol for either. Verified on
    /// macOS 26 with <c>dlsym</c>: the constants come back NULL while <c>IOPMAssertionCreateWithName</c> resolves, and
    /// passing this literal returns <c>kIOReturnSuccess</c> and a live assertion id. Looking the constant up through
    /// <see cref="NativeLibrary.GetExport"/> therefore throws <see cref="EntryPointNotFoundException"/> at the first
    /// acquire — which is exactly what happened before this was a string.
    /// </remarks>
    public const string AssertionTypePreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";

    [LibraryImport(NativeLibraries.IOKit)]
    internal static partial int IOPMAssertionCreateWithName(nint assertionType, uint level, nint assertionName, uint* assertionId);

    [LibraryImport(NativeLibraries.IOKit)]
    internal static partial int IOPMAssertionRelease(uint assertionId);
}
