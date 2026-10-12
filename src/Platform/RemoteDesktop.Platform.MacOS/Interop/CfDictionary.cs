using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>
/// Builds a CoreFoundation dictionary for the many Apple APIs that take one, and releases everything it created.
/// </summary>
/// <remarks>
/// Apple's C APIs take dictionaries of CFString keys to CFType values, which in C# is a pile of paired arrays and
/// manual <c>CFRelease</c> calls at every call site. This keeps the pairing and the ownership in one place so a caller
/// writes what it means and cannot forget to release the wrappers it made.
/// </remarks>
internal sealed class CfDictionary : IDisposable
{
    private readonly List<nint> _keys = [];
    private readonly List<nint> _values = [];
    private readonly List<nint> _created = [];
    private nint _handle;

    /// <summary>Adds a string value. The wrapper is owned by this dictionary.</summary>
    public CfDictionary AddString(nint key, string value)
    {
        var wrapper = CoreFoundation.CreateString(value);
        _created.Add(wrapper);
        return AddRaw(key, wrapper);
    }

    /// <summary>Adds an integer value. The wrapper is owned by this dictionary.</summary>
    public CfDictionary AddNumber(nint key, int value)
    {
        var wrapper = CoreFoundation.CreateNumber(value);
        _created.Add(wrapper);
        return AddRaw(key, wrapper);
    }

    /// <summary>Adds a boolean. The two CFBoolean objects are immortal singletons, so nothing is owned.</summary>
    public CfDictionary AddBoolean(nint key, bool value) => AddRaw(key, CoreFoundation.Boolean(value));

    /// <summary>Adds a byte buffer as CFData. The wrapper is owned by this dictionary.</summary>
    public CfDictionary AddData(nint key, ReadOnlySpan<byte> value)
    {
        nint wrapper;
        unsafe
        {
            fixed (byte* p = value)
            {
                wrapper = CoreFoundation.CFDataCreate(CoreFoundation.DefaultAllocator, p, value.Length);
            }
        }

        _created.Add(wrapper);
        return AddRaw(key, wrapper);
    }

    /// <summary>Adds a value the caller owns (a CFType it created or retained).</summary>
    public CfDictionary AddRaw(nint key, nint value)
    {
        _keys.Add(key);
        _values.Add(value);
        return this;
    }

    /// <summary>Materializes the dictionary. The result is owned by this object and released on <see cref="Dispose"/>.</summary>
    public nint Build()
    {
        if (_handle != 0)
        {
            return _handle;
        }

        unsafe
        {
            fixed (nint* keys = CollectionsMarshal.AsSpan(_keys))
            fixed (nint* values = CollectionsMarshal.AsSpan(_values))
            {
                _handle = CoreFoundation.CFDictionaryCreate(
                    CoreFoundation.DefaultAllocator,
                    keys,
                    values,
                    _keys.Count,
                    CoreFoundation.DefaultCallbacks,
                    CoreFoundation.DefaultCallbacks);
            }
        }

        return _handle;
    }

    public void Dispose()
    {
        foreach (var value in _created)
        {
            CoreFoundation.CFRelease(value);
        }

        _created.Clear();

        if (_handle != 0)
        {
            CoreFoundation.CFRelease(_handle);
            _handle = 0;
        }
    }
}
