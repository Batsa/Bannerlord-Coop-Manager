# ============================================================
# Publish-SingleExe.ps1
# ============================================================
#
# This script asks the .NET SDK to create a Release build for Windows x64.
#
# --self-contained true
#     Bundles the .NET runtime so the destination machine does not need to
#     install .NET separately.
#
# PublishSingleFile=true
#     Packs the managed application into a single primary Bannerlord Coop Manager.exe.
#
# IncludeNativeLibrariesForSelfExtract=true
#     Allows native WPF/runtime components to be included in single-file
#     publishing.
#
# Run from PowerShell:
#
#   powershell.exe -ExecutionPolicy Bypass -File .\Publish-SingleExe.ps1
#
# ============================================================

$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "BCSTool.csproj"
$PublishDirectory = Join-Path $PSScriptRoot "publish"
$RepositoryRoot = Split-Path $PSScriptRoot -Parent

if (Test-Path $PublishDirectory) {
    Remove-Item $PublishDirectory -Recurse -Force
}

dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $PublishDirectory `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false

$Executable = Join-Path $PublishDirectory "Bannerlord Coop Manager.exe"
$RuntimeBootstrap = Join-Path $PublishDirectory "BCSTool.RuntimeBootstrap.dll"

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Publish did not produce the application executable: $Executable"
}

if (-not (Test-Path -LiteralPath $RuntimeBootstrap -PathType Leaf)) {
    throw "Publish did not produce the required runtime bootstrap: $RuntimeBootstrap"
}

$CompanionFiles = @(
    "START-HERE.txt",
    "README.md",
    "LICENSE",
    "NOTICE.md"
)
foreach ($Name in $CompanionFiles) {
    $Source = Join-Path $RepositoryRoot $Name
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Publish companion file is missing: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $PublishDirectory
}

Write-Host ""
Write-Host "Publish complete:"
Write-Host "  $PublishDirectory"
Write-Host "  $Executable"
Write-Host "  $RuntimeBootstrap"
foreach ($Name in $CompanionFiles) {
    Write-Host "  $(Join-Path $PublishDirectory $Name)"
}
Write-Host ""
Write-Host "Bannerlord Coop Manager settings are stored in:"
Write-Host "  HKEY_CURRENT_USER\Software\BCSServerTool"
Write-Host ""
Write-Host "No settings.json file is required."
