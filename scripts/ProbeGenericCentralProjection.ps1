param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,
    [string]$BannerlordRoot = '',
    [ValidateRange(30, 300)]
    [int]$DurationSeconds = 90,
    [string]$OutputLog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\generic-central-probe.stdout.txt'),
    [switch]$SkipCentralProjection,
    [switch]$SuppressEarlyFrameworkSubModules,
    [switch]$IncludeOfficialClientDependencies
)

$ErrorActionPreference = 'Stop'
$canonicalServerRoot = [IO.Path]::GetFullPath($ServerRoot).TrimEnd('\')
$central = [IO.Path]::GetFullPath(
    (Join-Path $canonicalServerRoot 'engine\bin\Win64_Shipping_Server'))
$centralPrefix = $central.TrimEnd('\') + '\'
if (-not $central.StartsWith(
        $canonicalServerRoot + '\engine\bin\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe central projection directory: $central"
}

# Dependency modules precede their consumers so their shared assembly wins.
$orderedFolders = @(
    '2859188632', # Harmony
    '2859232415', # ButterLib
    '2859222409', # UIExtenderEx
    '2859238197', # MCM
    '2875138158'  # Xorberax Legacy
)
$chosen = @{}
foreach ($folder in $orderedFolders) {
    $bin = [IO.Path]::GetFullPath(
        (Join-Path $canonicalServerRoot "engine\Modules\$folder\bin\Win64_Shipping_Server"))
    if (-not $bin.StartsWith(
            $canonicalServerRoot + '\engine\Modules\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe module projection source: $bin"
    }
    foreach ($file in Get-ChildItem -LiteralPath $bin -File -Filter '*.dll') {
        if (-not $chosen.ContainsKey($file.Name)) {
            $chosen[$file.Name] = $file.FullName
        }
    }
}
$coopBin = [IO.Path]::GetFullPath(
    (Join-Path $canonicalServerRoot 'engine\Modules\Coop\bin\Win64_Shipping_Server'))
foreach ($name in @($chosen.Keys)) {
    $coopCandidate = Join-Path $coopBin $name
    if (Test-Path -LiteralPath $coopCandidate -PathType Leaf) {
        # The released dedicated-server package carries a .NET 6-compatible
        # Harmony/MonoMod stack. Prefer it for shared identities over client
        # framework builds with the same assembly version.
        $chosen[$name] = $coopCandidate
    }
}
if ($IncludeOfficialClientDependencies) {
    if ([string]::IsNullOrWhiteSpace($BannerlordRoot)) {
        throw '-BannerlordRoot is required with -IncludeOfficialClientDependencies.'
    }
    foreach ($dependencyPath in @(
        (Join-Path $BannerlordRoot 'bin\Win64_Shipping_Client\TaleWorlds.TwoDimension.dll'),
        (Join-Path $BannerlordRoot 'Modules\StoryMode\bin\Win64_Shipping_Client\StoryMode.dll'),
        (Join-Path $BannerlordRoot 'Modules\SandBox\bin\Win64_Shipping_Client\SandBox.View.dll'),
        (Join-Path $BannerlordRoot 'Modules\Native\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.View.dll')
    )) {
        if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
            throw "Official client dependency is missing: $dependencyPath"
        }
        $chosen[[IO.Path]::GetFileName($dependencyPath)] = $dependencyPath
    }
}

$created = [Collections.Generic.List[object]]::new()
$originalManifests = [Collections.Generic.List[object]]::new()
try {
    if ($SuppressEarlyFrameworkSubModules) {
        foreach ($folder in $orderedFolders[0..3]) {
            $manifestPath = [IO.Path]::GetFullPath(
                (Join-Path $canonicalServerRoot "engine\Modules\$folder\SubModule.xml"))
            $originalManifests.Add([pscustomobject]@{
                Path = $manifestPath
                Bytes = [IO.File]::ReadAllBytes($manifestPath)
            })
            [xml]$manifestDocument = [Text.Encoding]::UTF8.GetString(
                $originalManifests[$originalManifests.Count - 1].Bytes)
            foreach ($subModule in @($manifestDocument.Module.SubModules.SubModule)) {
                $tags = $subModule.SelectSingleNode('Tags')
                if (-not $tags) {
                    $tags = $manifestDocument.CreateElement('Tags')
                    [void]$subModule.AppendChild($tags)
                }
                foreach ($setting in @(
                    @{ Key = 'DedicatedServerType'; Value = 'none' },
                    @{ Key = 'IsNoRenderModeElement'; Value = 'false' }
                )) {
                    $tag = @($tags.SelectNodes('Tag')) |
                        Where-Object { $_.GetAttribute('key') -eq $setting.Key } |
                        Select-Object -First 1
                    if (-not $tag) {
                        $tag = $manifestDocument.CreateElement('Tag')
                        $tag.SetAttribute('key', $setting.Key)
                        [void]$tags.AppendChild($tag)
                    }
                    $tag.SetAttribute('value', $setting.Value)
                }
            }
            $manifestDocument.Save($manifestPath)
        }
        "TEMP_FRAMEWORK_MANIFESTS_SUPPRESSED=$($originalManifests.Count)"
    }

    foreach ($name in $(if ($SkipCentralProjection) { @() } else { $chosen.Keys })) {
        if ([IO.Path]::GetFileName($name) -ne $name) {
            throw "Unsafe projected assembly name: $name"
        }
        $target = [IO.Path]::GetFullPath((Join-Path $central $name))
        if (-not $target.StartsWith($centralPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Projected assembly escaped the central directory: $target"
        }
        if (Test-Path -LiteralPath $target) {
            continue
        }
        [IO.File]::Copy($chosen[$name], $target, $false)
        $created.Add([pscustomobject]@{
            Path = $target
            Hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        })
    }

    "TEMP_CENTRAL_PROJECTED=$($created.Count)"
    & (Join-Path $PSScriptRoot 'SmokeEurope1700Server.ps1') `
        -WorkspaceRoot $WorkspaceRoot `
        -ServerRoot $canonicalServerRoot `
        -FreshSave `
        -DurationSeconds $DurationSeconds `
        -OutputLog $OutputLog
}
finally {
    foreach ($originalManifest in $originalManifests) {
        [IO.File]::WriteAllBytes($originalManifest.Path, $originalManifest.Bytes)
    }
    "TEMP_FRAMEWORK_MANIFESTS_RESTORED=$($originalManifests.Count)"
    $removed = 0
    foreach ($entry in $created) {
        if (-not $entry.Path.StartsWith(
                $centralPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unsafe projection target: $($entry.Path)"
        }
        if ((Test-Path -LiteralPath $entry.Path) -and
            (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -eq $entry.Hash) {
            Remove-Item -LiteralPath $entry.Path -Force
            $removed++
        }
    }
    "TEMP_CENTRAL_REMOVED=$removed EXPECTED=$($created.Count)"
}
