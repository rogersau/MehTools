# Build the distributable tools from checksum-pinned upstream sources. No system installation.
[CmdletBinding()]
param([ValidateRange(1, 32)][int]$Jobs = 6)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo 'artifacts/native-tools'
$cache = Join-Path $work 'cache'
$sources = Join-Path $work 'sources'
$output = Join-Path $repo 'third_party/native-tools/win-x64'
$manifestPath = Join-Path $repo 'third_party/native-tools/manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
New-Item $cache, $sources, $output -ItemType Directory -Force | Out-Null
$inputs = (Get-Content $manifestPath -Raw) + (Get-Content "$PSScriptRoot/build-native-tools.sh" -Raw) + (Get-Content $PSCommandPath -Raw)
$inputHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inputs)))
$stamp = Join-Path $work 'build-inputs.sha256'
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -ne $inputHash) {
    # Changed sources/recipe must not reuse objects from an older native build.
    $ownedRoots = @([IO.Path]::GetFullPath($work), [IO.Path]::GetFullPath((Join-Path $repo 'third_party/native-tools')))
    $stale = @($sources, (Join-Path $work 'prefix'), (Join-Path $work 'source-distribution'), $output) + @(Get-ChildItem $work -Directory -Filter 'build-*' | ForEach-Object FullName)
    foreach ($path in $stale) {
        $absolute = [IO.Path]::GetFullPath($path)
        if (!($ownedRoots | Where-Object { $absolute.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) { throw "Refusing to clean outside native build folders: $absolute" }
        if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
    }
    New-Item $sources, $output -ItemType Directory -Force | Out-Null
}
foreach ($name in $manifest.Keys) {
    $item = $manifest[$name]; $file = Join-Path $cache $item.file
    if (!(Test-Path $file)) { Invoke-WebRequest $item.url -OutFile $file }
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $item.sha256) { throw "Checksum mismatch: $name. Remove $file and retry." }
    if ($item.ContainsKey('strip')) {
        $dest = Join-Path $sources $name
        New-Item $dest -ItemType Directory -Force | Out-Null
        & tar -xf $file -C $dest --strip-components $item.strip
        if ($LASTEXITCODE) { throw "Extraction failed: $name" }
    }
}
$toolchain = Join-Path $work 'toolchain/w64devkit'
if (!(Test-Path "$toolchain/bin/gcc.exe")) {
    $process = Start-Process -FilePath (Join-Path $cache 'w64devkit.7z.exe') -ArgumentList '-y', "-o`"$work/toolchain`"" -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode) { throw 'Toolchain extraction failed.' }
}
$previousPath = $env:PATH
try {
    $gitRoot = Split-Path (Split-Path (Get-Command git.exe -ErrorAction Stop).Source -Parent) -Parent
    $gitUtilities = @($gitRoot, (Split-Path $gitRoot -Parent)) | ForEach-Object { Join-Path $_ 'usr/bin' } | Where-Object { Test-Path "$_/perl.exe" } | Select-Object -First 1
    if (!(Test-Path "$gitUtilities/perl.exe")) { throw 'Git for Windows with its bundled Perl is required to build libaom.' }
    $env:PATH = "$toolchain/bin;$sources/nasm;$gitUtilities;$env:PATH"
    # The shell build uses forward slashes even on Windows. Arguments remain separate.
    & "$toolchain/bin/sh.exe" (Join-Path $PSScriptRoot 'build-native-tools.sh').Replace('\', '/') $work.Replace('\', '/') $output.Replace('\', '/') $Jobs
    if ($LASTEXITCODE) { throw "Native build failed ($LASTEXITCODE)." }
    $licenses = Join-Path $output 'licenses'
    New-Item $licenses -ItemType Directory -Force | Out-Null
    $notices = @{
        'ffmpeg/COPYING.LGPLv2.1' = 'FFmpeg-LGPL-2.1.txt'
        'aom/LICENSE' = 'AOM-LICENSE.txt'; 'aom/PATENTS' = 'AOM-PATENTS.txt'
        'zlib/LICENSE' = 'zlib.txt'
    }
    foreach ($entry in $notices.GetEnumerator()) { Copy-Item -LiteralPath (Join-Path $sources $entry.Key) -Destination (Join-Path $licenses $entry.Value) }
    Copy-Item -LiteralPath "$toolchain/COPYING.MinGW-w64-runtime.txt" -Destination $licenses
    Copy-Item -LiteralPath $manifestPath -Destination "$licenses/manifest.json"
    Copy-Item -LiteralPath (Join-Path $repo 'third_party/native-tools/README.md') -Destination "$licenses/README.md"
    # Corresponding source and recipe travel with every release, separately from the app download.
    $distribution = Join-Path $work 'source-distribution'
    New-Item $distribution -ItemType Directory -Force | Out-Null
    foreach ($item in $manifest.Values) {
        if (!$item.buildOnly) { Copy-Item -LiteralPath (Join-Path $cache $item.file) -Destination $distribution }
    }
    Copy-Item -LiteralPath $manifestPath, "$PSScriptRoot/build-native-tools.sh", $PSCommandPath, "$licenses/README.md" -Destination $distribution
    Compress-Archive -Path "$distribution/*" -DestinationPath (Join-Path $repo 'artifacts/MehTools-native-sources.zip') -Force
    Set-Content -LiteralPath $stamp -Value $inputHash -NoNewline
    Write-Host 'Native tools and corresponding source archive are ready.'
}
finally { $env:PATH = $previousPath }
