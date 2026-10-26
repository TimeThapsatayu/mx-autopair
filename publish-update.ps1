# Builds MxAutoPair, signs it and publishes it as a GitHub release.
# Installed copies pick it up within about a minute.
#   powershell -ExecutionPolicy Bypass -File .\publish-update.ps1 1.2.0
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = 'TimeThapsatayu/mx-autopair'
$key  = Join-Path $env:USERPROFILE '.mxautopair\update-signing-key.pem'
$out  = Join-Path $PSScriptRoot "release\$Version"

if (-not (Test-Path $key)) { throw "Signing key not found at $key" }
if (Test-Path $out) { throw "$out already exists; pick a new version number" }

# Trimmed single-file build: the variant that has completed a real pairing.
dotnet publish "$PSScriptRoot\MxAutoPair.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Remove-Item (Join-Path $out '*.pdb') -ErrorAction SilentlyContinue

$exe = Join-Path $out 'MxAutoPair.exe'
$manifest = Join-Path $out 'update.json'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
# Written once and signed as-is; the updater verifies these exact bytes.
[IO.File]::WriteAllText($manifest, "{`"version`":`"$Version`",`"sha256`":`"$hash`",`"size`":$((Get-Item $exe).Length)}")

dotnet run -c Release --project "$PSScriptRoot\tools\UpdateSigner" -- sign $key $manifest
if ($LASTEXITCODE -ne 0) { throw 'Signing failed' }

gh release create "v$Version" $exe $manifest "$manifest.sig" --repo $repo --title "v$Version" --notes "MxAutoPair $Version"
if ($LASTEXITCODE -ne 0) { throw 'Release failed' }
Write-Host "Published v$Version"
