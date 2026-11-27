# Build DrawingHelper.App + package into single-file DrawingHelperSetup.exe
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj = Join-Path $Root "DrawingHelper.App\DrawingHelper.App.csproj"
$SetupProj = Join-Path $Root "DrawingHelper.Setup\DrawingHelper.Setup.csproj"
$PayloadDir = Join-Path $Root "DrawingHelper.Setup\Payload"
$StageDir = Join-Path $env:TEMP ("dh_stage_" + [Guid]::NewGuid().ToString("N"))
$PayloadZip = Join-Path $PayloadDir "app_payload.zip"
$OutDir = Join-Path $Root "installer_out"
$PortableZip = Join-Path $OutDir "DrawingHelper-win-x64-portable.zip"

try {
    Write-Host "[1/3] Building DrawingHelper.App (Release)..."
    dotnet build $AppProj -c Release -o $StageDir -m:1 --disable-build-servers --tl:off -v minimal

    # Strip debug symbols for a clean release payload
    Get-ChildItem $StageDir -Filter "*.pdb" | Remove-Item -Force

    Write-Host "[2/3] Packaging app_payload.zip & portable ZIP..."
    New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
    if (Test-Path $PayloadZip) { Remove-Item -Force $PayloadZip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($StageDir, $PayloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    [System.IO.Compression.ZipFile]::CreateFromDirectory($StageDir, $PortableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue }
}

Write-Host "[3/3] Building single-file DrawingHelperSetup.exe..."
dotnet publish $SetupProj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $OutDir -m:1 --disable-build-servers --tl:off -v minimal

$SetupExe = Join-Path $OutDir "DrawingHelperSetup.exe"
$Info = Get-Item $SetupExe
$PortableInfo = Get-Item $PortableZip
$SetupHash = (Get-FileHash -Algorithm SHA256 $SetupExe).Hash
$PortableHash = (Get-FileHash -Algorithm SHA256 $PortableZip).Hash
@(
    "$SetupHash  DrawingHelperSetup.exe"
    "$PortableHash  DrawingHelper-win-x64-portable.zip"
) | Out-File -FilePath (Join-Path $OutDir "SHA256SUMS.txt") -Encoding ascii
Write-Host ""
Write-Host "SUCCESS: $SetupExe ($([math]::Round($Info.Length / 1KB, 1)) KB)"
Write-Host "PORTABLE: $PortableZip ($([math]::Round($PortableInfo.Length / 1KB, 1)) KB)"
Write-Host "SHA-256 (Setup):    $SetupHash"
Write-Host "SHA-256 (Portable): $PortableHash"
