param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceRoot,
    [Parameter(Mandatory = $true)]
    [string] $ModuleRoot,
    [Parameter(Mandatory = $true)]
    [string] $ServerModulesRoot,

    [string] $RegressionDll
)

$ErrorActionPreference = 'Stop'
$temporaryRoot = Join-Path $env:TEMP ('bcs-workshop-plan-' + [guid]::NewGuid().ToString('N'))
$stagedModules = Join-Path $temporaryRoot 'engine\Modules'

function Get-ModuleIdentity([string] $root) {
    $manifest = Join-Path $root 'SubModule.xml'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Module has no root SubModule.xml: $root"
    }
    [xml] $document = Get-Content -LiteralPath $manifest -Raw
    $id = [string] $document.Module.Id.value
    if ([string]::IsNullOrWhiteSpace($id) -or
        [System.IO.Path]::GetFileName($id) -ne $id -or
        $id.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "Unsafe module ID in manifest: $id"
    }
    return $id
}

function Copy-FingerprintSurface([string] $source, [string] $destination) {
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    $sourceFull = [System.IO.Path]::GetFullPath($source)
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($sourceFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [System.IO.File]::GetAttributes($entry)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Linked module content is not safe to stage: $entry"
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
                continue
            }

            $sourcePrefix = $sourceFull.TrimEnd('\') + '\'
            if (!$entry.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Staged file escaped its module root: $entry"
            }
            $relative = $entry.Substring($sourcePrefix.Length)
            $extension = [System.IO.Path]::GetExtension($entry)
            $copy = $relative -eq 'SubModule.xml' -or
                $extension -ieq '.xml' -or
                $extension -ieq '.xslt' -or
                $extension -ieq '.xsl' -or
                $extension -ieq '.json'
            if (!$copy -and $extension -ieq '.dll') {
                $dllDirectory = Split-Path $relative -Parent
                $bin = Split-Path $dllDirectory -Leaf
                $parent = Split-Path (Split-Path $dllDirectory -Parent) -Leaf
                $copy = $parent -ieq 'bin' -and $bin -in @(
                    'Win64_Shipping_Server',
                    'Win64_Shipping_Client',
                    'Gaming.Desktop.x64_Shipping_Client')
            }
            if (!$copy) {
                continue
            }

            $target = Join-Path $destination $relative
            [System.IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
            [System.IO.File]::Copy($entry, $target, $false)
        }
    }
}

try {
    [System.IO.Directory]::CreateDirectory($stagedModules) | Out-Null
    $sourceServerBin = Join-Path (Split-Path $ServerModulesRoot -Parent) 'bin\Win64_Shipping_Server'
    $stagedServerBin = Join-Path $temporaryRoot 'engine\bin\Win64_Shipping_Server'
    [System.IO.Directory]::CreateDirectory($stagedServerBin) | Out-Null
    foreach ($runtimeFile in @(
        'default_new_game.sav',
        'TaleWorlds.CampaignSystem.dll',
        'TaleWorlds.Core.dll',
        'TaleWorlds.Library.dll',
        'TaleWorlds.Localization.dll',
        'TaleWorlds.ModuleManager.dll',
        'TaleWorlds.MountAndBlade.dll',
        'TaleWorlds.ObjectSystem.dll'
    )) {
        $sourceRuntimeFile = Join-Path $sourceServerBin $runtimeFile
        if (!(Test-Path -LiteralPath $sourceRuntimeFile -PathType Leaf)) {
            throw "Required server runtime file is missing: $sourceRuntimeFile"
        }
        [System.IO.File]::Copy(
            $sourceRuntimeFile,
            (Join-Path $stagedServerBin $runtimeFile),
            $false)
    }
    $executableDependencyIds = @(
        'Coop',
        'Bannerlord.Harmony',
        'Bannerlord.ButterLib',
        'Bannerlord.UIExtenderEx',
        'Bannerlord.MBOptionScreen'
    )
    foreach ($serverModule in Get-ChildItem -LiteralPath $ServerModulesRoot -Directory) {
        if (($serverModule.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            continue
        }
        $manifest = Join-Path $serverModule.FullName 'SubModule.xml'
        if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) {
            continue
        }
        $id = Get-ModuleIdentity $serverModule.FullName
        $destination = Join-Path $stagedModules $id
        if ($executableDependencyIds -icontains $id) {
            Copy-FingerprintSurface $serverModule.FullName $destination
        }
        else {
            [System.IO.Directory]::CreateDirectory($destination) | Out-Null
            [System.IO.File]::Copy($manifest, (Join-Path $destination 'SubModule.xml'), $false)
        }
    }

    $moduleId = Get-ModuleIdentity $ModuleRoot
    $moduleDestination = Join-Path $stagedModules $moduleId
    if (Test-Path -LiteralPath $moduleDestination) {
        throw "Staging collision for module ID: $moduleId"
    }
    Copy-FingerprintSurface $ModuleRoot $moduleDestination

    if ([string]::IsNullOrWhiteSpace($RegressionDll)) {
        $RegressionDll = Join-Path $WorkspaceRoot 'BCSTool.RegressionTests\bin\Release\net10.0-windows\BCSTool.RegressionTests.dll'
    }
    & dotnet $RegressionDll --compat-plan $temporaryRoot $moduleId
    exit $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = [System.IO.Path]::GetFullPath($temporaryRoot)
        $tempBase = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        if (!$resolved.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
