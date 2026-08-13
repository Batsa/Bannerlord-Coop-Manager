[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BannerlordRoot,
    [Parameter(Mandatory = $true)]
    [string] $CoopModuleRoot,
    [Parameter(Mandatory = $true)]
    [string] $Europe1700ModuleRoot,
    [ValidateRange(30, 300)]
    [int] $TimeoutSeconds = 180,
    [switch] $KeepGameRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-SharedText {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-ModuleIdentity {
    param([Parameter(Mandatory = $true)][string] $ModuleRoot)

    $manifestPath = Join-Path $ModuleRoot 'SubModule.xml'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Module manifest missing: $manifestPath"
    }
    [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
    return [pscustomobject]@{
        Id = [string] $manifest.Module.Id.value
        Version = [string] $manifest.Module.Version.value
        Root = (Resolve-Path -LiteralPath $ModuleRoot).Path
        Manifest = $manifest
    }
}

function Get-ProcessesUnderRoot {
    param([Parameter(Mandatory = $true)][string] $Root)

    $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $result = @{}
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        try {
            $path = $process.Path
            if ($path -and [System.IO.Path]::GetFullPath($path).StartsWith(
                    $canonicalRoot,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $result[$process.Id] = $process
            }
        }
        catch {
            # Protected or exited process; it cannot be classified by executable path.
        }
    }
    return $result
}

$clientBin = Join-Path $BannerlordRoot 'bin\Win64_Shipping_Client'
$executable = Join-Path $clientBin 'Bannerlord.Native.exe'
$modulesRoot = Join-Path $BannerlordRoot 'Modules'
$commonApplicationData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonApplicationData)
$logRoot = Join-Path $commonApplicationData 'Mount and Blade II Bannerlord\logs'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Real Bannerlord client executable missing: $executable"
}

$coop = Read-ModuleIdentity $CoopModuleRoot
$eoe = Read-ModuleIdentity $Europe1700ModuleRoot
if ($coop.Id -ne 'Coop') {
    throw "Expected Coop module, found '$($coop.Id)' at $($coop.Root)."
}
if ($eoe.Id -ne 'Europe1700') {
    throw "Expected Europe1700 module, found '$($eoe.Id)' at $($eoe.Root)."
}

$bridges = @(Get-ChildItem -LiteralPath $modulesRoot -Directory -Filter 'BCS.CoopBridge.*' |
    ForEach-Object { Read-ModuleIdentity $_.FullName })
if ($bridges.Count -ne 1) {
    throw "Expected exactly one installed BCS Coop bridge, found $($bridges.Count)."
}
$bridge = $bridges[0]
$bridgeDependencies = @($bridge.Manifest.Module.DependedModules.DependedModule |
    ForEach-Object { [string] $_.Id })
if (($bridgeDependencies -join '|') -ne 'Coop|Europe1700') {
    throw "Bridge manifest dependencies must be exactly Coop then Europe1700; found $($bridgeDependencies -join ', ')."
}

$communityModuleIds = @($coop.Id, $eoe.Id, $bridge.Id)
$activeModuleIds = @(
    'Native',
    'SandBoxCore',
    'CustomBattle',
    'Sandbox',
    'StoryMode'
) + $communityModuleIds
$moduleToken = '_MODULES_*' + ($activeModuleIds -join '*') + '*_MODULES_'
$expectedArgs = '/singleplayer ' + $moduleToken

$preexisting = Get-ProcessesUnderRoot $clientBin
if ($preexisting.Count -ne 0) {
    throw 'Bannerlord client is already running; close it before the startup regression.'
}

$progressPath = Join-Path ([System.IO.Path]::GetTempPath()) 'bcs-coop-bridge-startup-progress.log'
$failurePath = Join-Path ([System.IO.Path]::GetTempPath()) 'bcs-coop-bridge-startup-failure.log'
$progressLength = if (Test-Path -LiteralPath $progressPath) {
    (Get-Item -LiteralPath $progressPath).Length
} else { 0L }
$failureLength = if (Test-Path -LiteralPath $failurePath) {
    (Get-Item -LiteralPath $failurePath).Length
} else { 0L }
$startedAt = [DateTime]::UtcNow
$process = $null
$passed = $false
$latestLog = $null

try {
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('/singleplayer', $moduleToken) `
        -WorkingDirectory $clientBin `
        -PassThru
    Write-Output "STARTED_PID=$($process.Id)"
    Write-Output "COMMUNITY_MODULES=$($communityModuleIds -join ',')"
    Write-Output "MODULE_TOKEN=$moduleToken"

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 1
        $latestLog = Get-ChildItem -LiteralPath $logRoot -Filter 'rgl_log_*.txt' -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -match '^rgl_log_[0-9]+\.txt$' -and
                $_.LastWriteTimeUtc -ge $startedAt.AddSeconds(-2)
            } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        $logText = if ($latestLog) { Read-SharedText $latestLog.FullName } else { '' }
        $progressText = Read-SharedText $progressPath
        $newProgress = if ($progressText.Length -gt $progressLength) {
            $progressText.Substring([int] $progressLength)
        } else { '' }
        $failureText = Read-SharedText $failurePath
        $newFailure = if ($failureText.Length -gt $failureLength) {
            $failureText.Substring([int] $failureLength)
        } else { '' }

        $failurePatterns = @(
            'Could not load file or assembly ''Common',
            'ReflectionTypeLoadException',
            'Error while loading BCS Coop Bridge',
            'submodule could not be loaded correctly',
            'dependency conflict'
        )
        $startupFailure = $failurePatterns | Where-Object {
            $logText.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        } | Select-Object -First 1
        if ($newFailure.Length -ne 0) {
            throw "Bridge startup failure file changed:`n$newFailure"
        }
        if ($startupFailure) {
            throw "Bannerlord startup log contains '$startupFailure': $($latestLog.FullName)"
        }
        if ($logText.IndexOf('Command Args:', [System.StringComparison]::Ordinal) -ge 0 -and
            $logText.IndexOf("Command Args: $expectedArgs", [System.StringComparison]::Ordinal) -lt 0) {
            throw "Bannerlord started with unexpected module arguments: $($latestLog.FullName)"
        }
        if ($newProgress.Contains('Client Coop handler type registered') -and
            $newProgress.Contains('OnSubModuleLoad completed')) {
            if ($process.HasExited) {
                throw "Bannerlord exited before startup proof with code $($process.ExitCode)."
            }
            $passed = $true
            Write-Output "RGL_LOG=$($latestLog.FullName)"
            Write-Output 'PASS: real Bannerlord loaded Coop, Europe1700, and bridge; delayed Coop handler registered without early Common.dll resolution failure.'
            break
        }
        if ($process.HasExited) {
            throw "Bannerlord exited before bridge startup completed with code $($process.ExitCode)."
        }
    }
    if (-not $passed) {
        throw "Timed out after $TimeoutSeconds seconds waiting for real bridge startup proof."
    }
}
finally {
    if (-not $KeepGameRunning) {
        $current = Get-ProcessesUnderRoot $clientBin
        foreach ($entry in $current.GetEnumerator()) {
            if (-not $preexisting.ContainsKey($entry.Key)) {
                Stop-Process -Id $entry.Key -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
