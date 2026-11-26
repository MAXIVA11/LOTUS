# Builds LOTUS and packs it into dist\LOTUS-Setup.exe.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests
#
# Needs the .NET SDK (8 or newer). Nothing else.

param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$config = 'Release'

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

Step 'Building the app and the service'
Invoke-DotNet build "$root\src\Lotus.App\Lotus.App.csproj" -c $config -nologo -v minimal
Invoke-DotNet build "$root\src\Lotus.Guard\Lotus.Guard.csproj" -c $config -nologo -v minimal

if (-not $SkipTests) {
    Step 'Running the tests'
    Invoke-DotNet test "$root\tests\Lotus.Tests\Lotus.Tests.csproj" -c $config -nologo -v minimal
}

Step 'Collecting the files that get installed'
$stage = Join-Path $root 'build\payload'
$zip = Join-Path $root 'build\payload.zip'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

$appOut = Join-Path $root "src\Lotus.App\bin\$config"
$guardOut = Join-Path $root "src\Lotus.Guard\bin\$config"
$files = @(
    (Join-Path $appOut 'LOTUS.exe'),
    (Join-Path $appOut 'LOTUS.exe.config'),
    (Join-Path $appOut 'Lotus.Core.dll'),
    (Join-Path $guardOut 'LotusGuard.exe'),
    (Join-Path $guardOut 'LotusGuard.exe.config')
)
foreach ($file in $files) {
    if (-not (Test-Path $file)) { throw "Missing build output: $file" }
    Copy-Item $file $stage
}
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')

if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Step 'Building setup'
Invoke-DotNet build "$root\src\Lotus.Setup\Lotus.Setup.csproj" -c $config -nologo -v minimal

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist 'LOTUS-Setup.exe'
Copy-Item (Join-Path $root "src\Lotus.Setup\bin\$config\LOTUS-Setup.exe") $setup -Force

$size = [math]::Round((Get-Item $setup).Length / 1KB)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Step 'Done'
Write-Host "$setup  ($size KB)"
Write-Host "SHA-256  $hash"
