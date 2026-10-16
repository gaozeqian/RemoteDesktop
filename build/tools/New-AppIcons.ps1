<#
.SYNOPSIS
  Regenerates the client's icon assets from the Windows Remote Desktop Connection (mstsc.exe) icon.

.DESCRIPTION
  Windows only, deliberately: the master image is lifted out of mstsc.exe's resources with LoadLibraryEx, and the
  derived sizes are produced with System.Drawing. Neither exists off Windows, and neither needs to — the generated
  assets are committed, so this runs only when the icon itself changes. build/tools/README.md says so out loud, which
  is the point of keeping it here rather than at the root of build/.

.PARAMETER OutputDir
  Where the assets are written. Defaults to src/Hosts/RemoteDesktop.Client/Assets under the repository root.
#>
Param(
    [string]$OutputDir = ''
)

$ErrorActionPreference = "Stop"

# Two levels up: this script lives in build/tools/. The default output is resolved against the repository root rather
# than against whatever directory the caller happens to be in.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($OutputDir)) { $OutputDir = Join-Path $root 'src/Hosts/RemoteDesktop.Client/Assets' }

$outFull = [System.IO.Path]::GetFullPath($OutputDir)
$mstscPath = "C:\Windows\System32\mstsc.exe"

if (Test-Path $mstscPath) {
    Write-Output "Extracting official Windows Remote Desktop Connection (mstsc.exe) icon assets..."

    $csharp = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public struct GroupEntry
{
    public byte Width;
    public byte Height;
    public byte ColorCount;
    public byte Reserved;
    public ushort Planes;
    public ushort BitCount;
    public uint BytesInRes;
    public ushort Id;
}

public static class MstscIconExtractor
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private const uint RT_ICON = 3;
    private const uint RT_GROUP_ICON = 14;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameDelegate lpEnumFunc, IntPtr lParam);

    private delegate bool EnumResNameDelegate(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    public static void Extract(string exePath, string assetsDir)
    {
        IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hModule == IntPtr.Zero) return;

        IntPtr firstGroupIconName = IntPtr.Zero;
        EnumResourceNames(hModule, (IntPtr)RT_GROUP_ICON, delegate(IntPtr h, IntPtr type, IntPtr name, IntPtr param)
        {
            firstGroupIconName = name;
            return false;
        }, IntPtr.Zero);

        if (firstGroupIconName == IntPtr.Zero)
        {
            FreeLibrary(hModule);
            return;
        }

        IntPtr hResInfo = FindResource(hModule, firstGroupIconName, (IntPtr)RT_GROUP_ICON);
        IntPtr hResData = LoadResource(hModule, hResInfo);
        IntPtr pGroupIcon = LockResource(hResData);
        uint size = SizeofResource(hModule, hResInfo);

        byte[] groupBytes = new byte[size];
        Marshal.Copy(pGroupIcon, groupBytes, 0, (int)size);

        ushort idReserved;
        ushort idType;
        ushort idCount;
        GroupEntry[] entries;

        using (MemoryStream groupMs = new MemoryStream(groupBytes))
        using (BinaryReader groupBr = new BinaryReader(groupMs))
        {
            idReserved = groupBr.ReadUInt16();
            idType = groupBr.ReadUInt16();
            idCount = groupBr.ReadUInt16();

            entries = new GroupEntry[idCount];
            for (int i = 0; i < idCount; i++)
            {
                entries[i] = new GroupEntry();
                entries[i].Width = groupBr.ReadByte();
                entries[i].Height = groupBr.ReadByte();
                entries[i].ColorCount = groupBr.ReadByte();
                entries[i].Reserved = groupBr.ReadByte();
                entries[i].Planes = groupBr.ReadUInt16();
                entries[i].BitCount = groupBr.ReadUInt16();
                entries[i].BytesInRes = groupBr.ReadUInt32();
                entries[i].Id = groupBr.ReadUInt16();
            }
        }

        string outIco = Path.Combine(assetsDir, "icon.ico");
        using (FileStream fs = File.Create(outIco))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write(idReserved);
            bw.Write(idType);
            bw.Write(idCount);

            uint imageOffset = 6 + (uint)(16 * idCount);
            for (int i = 0; i < idCount; i++)
            {
                bw.Write(entries[i].Width);
                bw.Write(entries[i].Height);
                bw.Write(entries[i].ColorCount);
                bw.Write(entries[i].Reserved);
                bw.Write(entries[i].Planes);
                bw.Write(entries[i].BitCount);
                bw.Write(entries[i].BytesInRes);
                bw.Write(imageOffset);
                imageOffset += entries[i].BytesInRes;
            }

            for (int i = 0; i < idCount; i++)
            {
                IntPtr hIconInfo = FindResource(hModule, (IntPtr)entries[i].Id, (IntPtr)RT_ICON);
                IntPtr hIconData = LoadResource(hModule, hIconInfo);
                IntPtr pIcon = LockResource(hIconData);
                byte[] iconBytes = new byte[entries[i].BytesInRes];
                Marshal.Copy(pIcon, iconBytes, 0, (int)entries[i].BytesInRes);
                bw.Write(iconBytes);
            }
            bw.Flush();
        }

        for (int i = 0; i < idCount; i++)
        {
            IntPtr hIconInfo = FindResource(hModule, (IntPtr)entries[i].Id, (IntPtr)RT_ICON);
            IntPtr hIconData = LoadResource(hModule, hIconInfo);
            IntPtr pIcon = LockResource(hIconData);
            byte[] iconBytes = new byte[entries[i].BytesInRes];
            Marshal.Copy(pIcon, iconBytes, 0, (int)entries[i].BytesInRes);

            if (iconBytes.Length > 8 && iconBytes[0] == 0x89 && iconBytes[1] == 0x50 && iconBytes[2] == 0x4E && iconBytes[3] == 0x47)
            {
                // Written under its size name only. There used to be a second copy called icon.png, holding these same
                // bytes: mstsc's icon is 256x256, so the master and the 256 size were one image under two names.
                File.WriteAllBytes(Path.Combine(assetsDir, "icon-256.png"), iconBytes);
                break;
            }
        }

        FreeLibrary(hModule);
    }
}
'@
    if (-not ([System.Management.Automation.PSTypeName]'MstscIconExtractor').Type) {
        Add-Type -TypeDefinition $csharp -Language CSharp
    }
    [MstscIconExtractor]::Extract($mstscPath, $outFull)
}

# Generate the derived PNG sizes from the resource's own 256. That one is the master, so it is not in the list below.
$masterPng = Join-Path $outFull "icon-256.png"
if (Test-Path $masterPng) {
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::FromFile($masterPng)
    $sizes = 512, 128, 64, 48, 32, 24, 16
    foreach ($s in $sizes) {
        $resized = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($resized)
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($bmp, 0, 0, $s, $s)
        $g.Dispose()

        $resized.Save((Join-Path $outFull "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $resized.Dispose()
    }
    $bmp.Dispose()

    # Update icon.svg
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $outFull "icon-256.png"))
    $b64 = [Convert]::ToBase64String($bytes)
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="100%" height="100%">
  <image width="256" height="256" href="data:image/png;base64,$b64" />
</svg>
"@
    [System.IO.File]::WriteAllText((Join-Path $outFull "icon.svg"), $svg)
}

Write-Output "Successfully updated official Windows Remote Desktop Connection icon assets in $outFull"
