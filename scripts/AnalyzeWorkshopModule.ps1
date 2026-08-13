param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceRoot,
    [Parameter(Mandatory = $true)]
    [string] $ModuleRoot,
    [Parameter(Mandatory = $true)]
    [string] $ServerModulesRoot
)

$ErrorActionPreference = 'Stop'
$temporaryRoot = Join-Path $env:TEMP ('bcs-workshop-analysis-' + [guid]::NewGuid().ToString('N'))
$stagedModules = Join-Path $temporaryRoot 'engine\Modules'

try {
    [System.IO.Directory]::CreateDirectory($stagedModules) | Out-Null
    foreach ($serverModule in Get-ChildItem -LiteralPath $ServerModulesRoot -Directory) {
        if (($serverModule.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            continue
        }
        $manifest = Join-Path $serverModule.FullName 'SubModule.xml'
        if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) {
            continue
        }
        $stub = Join-Path $stagedModules $serverModule.Name
        [System.IO.Directory]::CreateDirectory($stub) | Out-Null
        Copy-Item -LiteralPath $manifest -Destination (Join-Path $stub 'SubModule.xml')
    }

    $sourceManifest = Join-Path $ModuleRoot 'SubModule.xml'
    if (!(Test-Path -LiteralPath $sourceManifest -PathType Leaf)) {
        throw "Module has no root SubModule.xml: $ModuleRoot"
    }
    [xml] $manifestDocument = Get-Content -LiteralPath $sourceManifest -Raw
    $moduleId = [string] $manifestDocument.Module.Id.value
    if ([string]::IsNullOrWhiteSpace($moduleId)) {
        throw "Module manifest has no ID: $sourceManifest"
    }
    if ([System.IO.Path]::GetFileName($moduleId) -ne $moduleId -or
        $moduleId.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "Unsafe module ID in manifest: $moduleId"
    }

    $destination = Join-Path $stagedModules $moduleId
    if (Test-Path -LiteralPath $destination) {
        throw "Staging collision for module ID: $moduleId"
    }
    Copy-Item -LiteralPath $ModuleRoot -Destination $destination -Recurse

    $regressionDll = Join-Path $WorkspaceRoot 'BCSTool.RegressionTests\bin\Release\net10.0-windows\BCSTool.RegressionTests.dll'
    & dotnet $regressionDll --compat-analyze $temporaryRoot $moduleId
    exit $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
