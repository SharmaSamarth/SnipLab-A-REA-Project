# Builds SnipLab with the C# compiler that ships with Windows (.NET Framework 4.x),
# so no SDK or Visual Studio is needed. The WinRT metadata files give access to
# the built-in OCR engine (Windows.Media.Ocr) used by Text actions.
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$references = @(
    'System.Windows.Forms.dll', 'System.Drawing.dll',
    "$framework\System.Runtime.dll", "$framework\System.Threading.Tasks.dll", "$framework\System.Runtime.WindowsRuntime.dll",
    "$framework\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$winmd\Windows.Foundation.winmd", "$winmd\Windows.Globalization.winmd", "$winmd\Windows.Graphics.winmd",
    "$winmd\Windows.Media.winmd", "$winmd\Windows.Security.winmd", "$winmd\Windows.Storage.winmd"
) | ForEach-Object { "/reference:$_" }

$sources = (Get-ChildItem (Join-Path $PSScriptRoot 'src\*.cs')).FullName
& "$framework\csc.exe" /nologo /target:winexe /optimize+ "/out:$out\SnipLab.exe" $references $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out\SnipLab.exe"
