using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// Minimal <c>ICodecAPI</c> client (not projected by Vortice): only <c>SetValue</c>, called through the vtable.
/// Encoders that do not implement the interface, or a given property, are tolerated: every call reports success.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // IUnknown (3) + IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue.
    private const int SetValueSlot = 9;

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;

    private nint _pointer;

    private CodecApi(nint pointer) => _pointer = pointer;

    public static CodecApi? TryCreate(ComObject transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var iid = IidCodecApi;
        return Marshal.QueryInterface(transform.NativePointer, in iid, out var pointer) == 0 ? new CodecApi(pointer) : null;
    }

    public bool SetUInt32(Guid property, uint value) => Set(property, new Variant { Type = VtUi4, UInt32 = value });

    public bool SetBool(Guid property, bool value) => Set(property, new Variant { Type = VtBool, Bool = value ? (short)-1 : (short)0 });

    private bool Set(Guid property, Variant value)
    {
        if (_pointer == 0)
        {
            return false;
        }

        var vtable = *(nint**)_pointer;
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)vtable[SetValueSlot];
        return setValue(_pointer, &property, &value) >= 0;
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            Marshal.Release(pointer);
        }
    }

    // VARIANT: 2-byte type, 6 reserved bytes, 16-byte union on 64-bit platforms.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public uint UInt32;

        [FieldOffset(8)]
        public short Bool;
    }
}
