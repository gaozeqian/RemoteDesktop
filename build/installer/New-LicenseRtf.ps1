<#
.SYNOPSIS
  Turns the license text of the installer into the RTF that WiX and the MSI license page expect.

.DESCRIPTION
  The license page of an MSI is an RTF control: it takes RTF, not plain text, and the RTF has to survive a code page
  nobody can predict on the target machine. So every character outside ASCII is written as an RTF Unicode escape
  (\uN?), which is ASCII on the wire and renders as the right character in any locale.

  The text itself is kept in License.txt, next to this script, because that is a file anybody can read and edit.

.EXAMPLE
  .\New-LicenseRtf.ps1                       # writes License.rtf next to License.txt
  .\New-LicenseRtf.ps1 -Text other.txt -Rtf other.rtf
#>
[CmdletBinding()]
param(
    # Empty means "next to this script"; the default is applied below.
    [string]$Text = '',
    [string]$Rtf = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1, so the default paths live here.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Text) { $Text = Join-Path $scriptDirectory 'License.txt' }
if (-not $Rtf) { $Rtf = Join-Path $scriptDirectory 'License.rtf' }

if (-not (Test-Path $Text)) { throw "The license text '$Text' was not found." }

$lines = Get-Content -LiteralPath $Text -Encoding utf8
$builder = [System.Text.StringBuilder]::new()

# A4-ish margins in twips, Segoe UI for Latin text and a CJK-capable fallback for the rest.
[void]$builder.AppendLine('{\rtf1\ansi\ansicpg1252\deff0')
[void]$builder.AppendLine('{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}{\f1\fswiss\fcharset134 Microsoft YaHei;}}')
[void]$builder.AppendLine('\viewkind4\uc1')

foreach ($line in $lines) {
    [void]$builder.Append('\pard\f0\fs18 ')

    foreach ($character in $line.ToCharArray()) {
        $code = [int]$character
        switch ($code) {
            0x5C { [void]$builder.Append('\\') }        # backslash
            0x7B { [void]$builder.Append('\{') }        # {
            0x7D { [void]$builder.Append('\}') }        # }
            default {
                if ($code -lt 128) {
                    [void]$builder.Append($character)
                }
                else {
                    # \uN? : the signed 16-bit value, with '?' as the fallback for readers that cannot do Unicode.
                    $signed = if ($code -gt 32767) { $code - 65536 } else { $code }
                    [void]$builder.Append('\u' + $signed + '?')
                }
            }
        }
    }

    [void]$builder.AppendLine('\par')
}

[void]$builder.AppendLine('}')

# RTF has to be written as ANSI for the escape sequences to mean what they mean; every non-ASCII byte is escaped.
[System.IO.File]::WriteAllText($Rtf, $builder.ToString(), [System.Text.Encoding]::ASCII)

$nonAscii = (Get-Content -LiteralPath $Rtf -Raw | Where-Object { $_ -match '[^\x00-\x7F]' }) -ne $null
Write-Host "License RTF written to $Rtf ($((Get-Item $Rtf).Length) bytes, non-ASCII passthrough: $nonAscii)"
