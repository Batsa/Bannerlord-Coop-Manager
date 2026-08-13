param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,
    [string]$CoopData = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)) 'Mount and Blade II Bannerlord\CoopData'),
    [string]$OutputLog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Europe1700-smoke.log'),
    [switch]$VanillaBaseline,
    [string[]]$DisabledSubmoduleDlls = @(),
    [string[]]$AdditionalManagedSearchDirectories = @(),
    [switch]$DetachedProbe,
    [switch]$FreshSave,
    [switch]$KeepFreshSave,
    [string]$ExistingSaveName,
    [switch]$TickProbe,
    [string[]]$ProbeCommands = @(),
    [ValidateRange(15, 3600)]
    [int]$DurationSeconds = 90
)

$ErrorActionPreference = 'Stop'
$ServerPort = 4200
$OutputLog = [IO.Path]::GetFullPath($OutputLog)

$engineRoot = Join-Path $ServerRoot 'engine'
$serverBin = Join-Path $engineRoot 'bin\Win64_Shipping_Server'
$dotnet = Join-Path $engineRoot 'dotnet\dotnet.exe'
$bootstrap = Join-Path $WorkspaceRoot 'BCSTool\bin\Release\net10.0-windows\BCSTool.RuntimeBootstrap.dll'
$config = Join-Path $CoopData 'DedicatedServer\server-config.json'
$userDirectory = Join-Path $CoopData 'DedicatedServer'
$eoeManifest = Join-Path $engineRoot 'Modules\3231544373\SubModule.xml'
$originalConfig = [IO.File]::ReadAllBytes($config)
$originalManifest = if (Test-Path -LiteralPath $eoeManifest) {
    [IO.File]::ReadAllBytes($eoeManifest)
}
else {
    $null
}
$previousEnvironment = @{}
$watchdogJob = $null
$outputWriter = $null
$crashLog = $OutputLog + '.crash.txt'
$engineOutputLog = $OutputLog + '.engine.txt'
$smokeSaveName = if ($FreshSave) {
    'bcs_eoe_smoke_' + [Guid]::NewGuid().ToString('N')
}
$configuredSaveName = if ($smokeSaveName) {
    $smokeSaveName
}
elseif ($ExistingSaveName) {
    $ExistingSaveName
}

if ($KeepFreshSave -and -not $FreshSave) {
    throw '-KeepFreshSave requires -FreshSave.'
}
elseif ($FreshSave -and $ExistingSaveName) {
    throw '-FreshSave and -ExistingSaveName are mutually exclusive.'
}
elseif ($TickProbe -and -not $ExistingSaveName) {
    throw '-TickProbe requires -ExistingSaveName.'
}
elseif ($TickProbe -and $DetachedProbe) {
    throw '-TickProbe and -DetachedProbe are mutually exclusive.'
}
elseif ($TickProbe -and $DurationSeconds -lt 45) {
    throw '-TickProbe requires -DurationSeconds of at least 45.'
}
else {
    $null
}
$smokeSaveRoot = Join-Path $userDirectory 'Game Saves'
if ($ExistingSaveName) {
    if ($ExistingSaveName -notmatch '^[A-Za-z0-9_-]{1,64}$') {
        throw '-ExistingSaveName must contain 1-64 letters, digits, underscores, or hyphens.'
    }
    foreach ($extension in @('.sav', '.json')) {
        $existingPath = Join-Path $smokeSaveRoot ($ExistingSaveName + $extension)
        $existingItem = Get-Item -LiteralPath $existingPath -ErrorAction SilentlyContinue
        if (-not $existingItem -or $existingItem.Length -le 0) {
            throw "Existing EOE save artifact is missing or empty: $existingPath"
        }
    }
}
$smokeSavePaths = if ($smokeSaveName) {
    @(
        (Join-Path $smokeSaveRoot ($smokeSaveName + '.sav')),
        (Join-Path $smokeSaveRoot ($smokeSaveName + '.json'))
    )
}
else {
    @()
}

function Get-Sha256Hex([byte[]]$bytes) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

$tickProbeOriginalSaveBytes = @{}
$tickProbeOriginalSaveHash = if ($TickProbe) {
    foreach ($extension in @('.sav', '.json')) {
        $tickProbeSavePath = Join-Path $smokeSaveRoot ($ExistingSaveName + $extension)
        $tickProbeOriginalSaveBytes[$tickProbeSavePath] =
            [IO.File]::ReadAllBytes($tickProbeSavePath)
    }
    Get-Sha256Hex $tickProbeOriginalSaveBytes[
        (Join-Path $smokeSaveRoot ($ExistingSaveName + '.sav'))]
}

if ($DetachedProbe -and -not ('BcsDetachedProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class BcsDetachedProcess
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static int Start(string applicationName, string arguments, string currentDirectory)
    {
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf(typeof(StartupInfo)),
            Flags = 0x00000001,
            ShowWindow = 0
        };
        var commandLine = new StringBuilder(
            "\"" + applicationName + "\" " + arguments);
        ProcessInformation process;
        const uint CreateNewProcessGroup = 0x00000200;
        const uint CreateNewConsole = 0x00000010;
        if (!CreateProcess(
                applicationName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateNewProcessGroup | CreateNewConsole,
                IntPtr.Zero,
                currentDirectory,
                ref startup,
                out process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        CloseHandle(process.Thread);
        CloseHandle(process.Process);
        return process.ProcessId;
    }
}
'@
}

try {
    $outputDirectory = Split-Path -Parent $OutputLog
    if ($outputDirectory) {
        [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }
    $outputWriter = [IO.StreamWriter]::new(
        $OutputLog,
        $false,
        [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $crashLog) {
        Remove-Item -LiteralPath $crashLog -Force
    }
    if (Test-Path -LiteralPath $engineOutputLog) {
        Remove-Item -LiteralPath $engineOutputLog -Force
    }

    $configText = [Text.Encoding]::UTF8.GetString($originalConfig)
    $configText = [Regex]::Replace(
        $configText,
        '("autosaveMinutes"\s*:\s*)\d+',
        '${1}0',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($configuredSaveName) {
        $configText = [Regex]::Replace(
            $configText,
            '("saveName"\s*:\s*")[^"]*(")',
            ('${1}' + $configuredSaveName + '${2}'),
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    $configText = [Regex]::Replace(
        $configText,
        '("steam"\s*:\s*)true',
        '${1}false',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    [IO.File]::WriteAllText($config, $configText, [Text.UTF8Encoding]::new($false))

    if (-not $VanillaBaseline -and $DisabledSubmoduleDlls.Count -gt 0) {
        [xml]$manifestXml = [Text.Encoding]::UTF8.GetString($originalManifest)
        foreach ($dllName in $DisabledSubmoduleDlls) {
            $submodule = @($manifestXml.Module.SubModules.SubModule) |
                Where-Object { $_.DLLName.value -eq $dllName } |
                Select-Object -First 1
            if (-not $submodule) {
                throw "EOE submodule not found for smoke isolation: $dllName"
            }

            $tags = $submodule.SelectSingleNode('Tags')
            if (-not $tags) {
                $tags = $manifestXml.CreateElement('Tags')
                [void]$submodule.AppendChild($tags)
            }
            $serverTag = @($tags.SelectNodes('Tag')) |
                Where-Object { $_.GetAttribute('key') -eq 'DedicatedServerType' } |
                Select-Object -First 1
            if (-not $serverTag) {
                $serverTag = $manifestXml.CreateElement('Tag')
                $serverTag.SetAttribute('key', 'DedicatedServerType')
                [void]$tags.AppendChild($serverTag)
            }
            $serverTag.SetAttribute('value', 'none')

            $headlessTag = @($tags.SelectNodes('Tag')) |
                Where-Object { $_.GetAttribute('key') -eq 'IsNoRenderModeElement' } |
                Select-Object -First 1
            if (-not $headlessTag) {
                $headlessTag = $manifestXml.CreateElement('Tag')
                $headlessTag.SetAttribute('key', 'IsNoRenderModeElement')
                [void]$tags.AppendChild($headlessTag)
            }
            $headlessTag.SetAttribute('value', 'false')
        }
        $manifestXml.Save($eoeManifest)
    }

    $enabledModuleIds = if ($VanillaBaseline) {
        @('Native', 'DedicatedServer.Windows', 'SandBoxCore', 'Sandbox', 'Coop')
    }
    else {
        $moduleProfile = Get-Content `
            -LiteralPath (Join-Path $ServerRoot 'bcs-server-modules.json') `
            -Raw |
            ConvertFrom-Json
        if ($moduleProfile.SchemaVersion -ne 1) {
            throw 'Unsupported server module profile schema.'
        }
        $enabled = @($moduleProfile.Modules |
            Where-Object { $_.Enabled } |
            ForEach-Object { $_.Id })
        $enabledBridges = @($enabled |
            Where-Object { $_.StartsWith('BCS.CoopBridge.', [StringComparison]::OrdinalIgnoreCase) })
        if ($enabledBridges.Count -ne 1) {
            throw "Expected one enabled Coop bridge, found $($enabledBridges.Count)."
        }
        $enabled
    }
    $moduleRoots = [Collections.Generic.List[string]]::new()
    $modulesDirectory = Join-Path $engineRoot 'Modules'
    foreach ($enabledModuleId in $enabledModuleIds) {
        $moduleRoot = Join-Path $modulesDirectory $enabledModuleId
        if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
            $moduleRoot = Get-ChildItem -LiteralPath $modulesDirectory -Directory |
                Where-Object {
                    $candidateManifest = Join-Path $_.FullName 'SubModule.xml'
                    if (-not (Test-Path -LiteralPath $candidateManifest -PathType Leaf)) {
                        return $false
                    }
                    [xml]$candidateXml = Get-Content -LiteralPath $candidateManifest -Raw
                    return $candidateXml.Module.Id.value -eq $enabledModuleId
                } |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
            throw "Enabled module directory not found: $moduleRoot"
        }
        $moduleRoots.Add($moduleRoot)
    }
    $searchDirectories = [Collections.Generic.List[string]]::new()
    $searchDirectories.Add($serverBin)
    foreach ($moduleRoot in $moduleRoots) {
        foreach ($relativeBin in @(
            'bin\Win64_Shipping_Server',
            'bin\Win64_Shipping_Client',
            'bin\Gaming.Desktop.x64_Shipping_Client')) {
            $candidate = Join-Path $moduleRoot $relativeBin
            if (Test-Path -LiteralPath $candidate) {
                $searchDirectories.Add($candidate)
            }
        }
    }
    $managedDependencyProfile = Join-Path $ServerRoot 'bcs-managed-dependencies.json'
    if (Test-Path -LiteralPath $managedDependencyProfile) {
        $managedDependencies = Get-Content -LiteralPath $managedDependencyProfile -Raw |
            ConvertFrom-Json
        if ($managedDependencies.SchemaVersion -ne 1) {
            throw "Unsupported managed dependency profile: $managedDependencyProfile"
        }
        foreach ($directoryEntry in $managedDependencies.Directories) {
            $managedDirectory = [IO.Path]::GetFullPath($directoryEntry.Path)
            foreach ($requiredFile in $directoryEntry.RequiredFiles) {
                $requiredPath = Join-Path $managedDirectory $requiredFile.Name
                if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                    throw "Pinned managed dependency is missing: $requiredPath"
                }
                $actualHash = Get-Sha256Hex ([IO.File]::ReadAllBytes($requiredPath))
                if ($actualHash -ne $requiredFile.Sha256) {
                    throw "Pinned managed dependency changed: $requiredPath"
                }
            }
            $searchDirectories.Add($managedDirectory)
        }
    }
    foreach ($additionalDirectory in $AdditionalManagedSearchDirectories) {
        if (-not (Test-Path -LiteralPath $additionalDirectory -PathType Container)) {
            throw "Additional managed search directory not found: $additionalDirectory"
        }
        $searchDirectories.Add([IO.Path]::GetFullPath($additionalDirectory))
    }

    $launchEnvironment = @{
        WINDIR = [Environment]::GetEnvironmentVariable('SystemRoot')
        DOTNET_ROOT = (Join-Path $engineRoot 'dotnet')
        DOTNET_MULTILEVEL_LOOKUP = '0'
        DOTNET_STARTUP_HOOKS = $bootstrap
        BANNERLORD_USER_DIR = $userDirectory
        COOP_DATA_DIR = $CoopData
        BCSTOOL_DS_CRASH_LOG = $crashLog
        BCSTOOL_DS_MANAGED_SEARCH_DIRECTORIES =
            ($searchDirectories -join [IO.Path]::PathSeparator)
        BCSTOOL_BRIDGE_TRACE_LOG = "$OutputLog.bridge.txt"
    }
    if ($DetachedProbe -or $TickProbe) {
        $launchEnvironment['BCSTOOL_DS_CONSOLE_LOG'] = $engineOutputLog
    }
    $bridgeTraceLog = "$OutputLog.bridge.txt"
    if (Test-Path -LiteralPath $bridgeTraceLog) {
        Remove-Item -LiteralPath $bridgeTraceLog -Force
    }
    foreach ($entry in $launchEnvironment.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }

    $moduleToken = '_MODULES_*' + ($enabledModuleIds -join '*') + '*_MODULES_'
    if (-not $DetachedProbe) {
        $watchdogDelay = if ($TickProbe) { $DurationSeconds + 20 } else { $DurationSeconds }
        $watchdogJob = Start-Job -ArgumentList $ServerRoot, $watchdogDelay -ScriptBlock {
            param($ExactServerRoot, $Seconds)
            Start-Sleep -Seconds $Seconds
            $targets = Get-CimInstance Win32_Process | Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.StartsWith(
                    $ExactServerRoot,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            foreach ($target in $targets) {
                Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue
            }
            return @($targets).Count
        }
    }

    $signalPattern =
        'MissingMethodException|CSharpCompilationOptions|HostSaveGame|LoadGame|' +
        'saveName|Loading save|Loaded save|GameServer\.Init|BCS Coop Bridge|' +
        'Unhandled|Exception|FATAL|crash|listening|Direct connect|' +
        "UDP $ServerPort|Coop server|resolver active|@DS@"
    $roslynMismatch = $false
    $detachedProbeHealthy = $true
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    'SIGNALS_BEGIN'
    if ($smokeSaveName) {
        "SMOKE_SAVE_NAME=$smokeSaveName"
    }
    if ($DetachedProbe) {
        $probeStarted = Get-Date
        $serverArguments = @(
            'TaleWorlds.Starter.DotNetCore.dll',
            $moduleToken,
            '/dedicatedcustomserver',
            $ServerPort.ToString([Globalization.CultureInfo]::InvariantCulture),
            'EU',
            '0')
        $serverArgumentLine = ($serverArguments |
            ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
        $serverProcessId = [BcsDetachedProcess]::Start(
            $dotnet,
            $serverArgumentLine,
            $serverBin)
        do {
            Start-Sleep -Milliseconds 500
            $serverProcessAlive = Get-Process `
                -Id $serverProcessId `
                -ErrorAction SilentlyContinue
        } while (
            $serverProcessAlive -and
            $stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds)
        $stopwatch.Stop()
        $serverExitCode = $null
        $crashEvent = Get-WinEvent `
            -FilterHashtable @{
                LogName = 'Application'
                Id = 1000
                StartTime = $probeStarted
            } `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Message -like "*Faulting application path: $dotnet*"
            } |
            Select-Object -First 1
        $servingReached =
            (Test-Path -LiteralPath $engineOutputLog) -and
            [bool](Select-String `
                -LiteralPath $engineOutputLog `
                -Pattern 'SERVING|phase serving|coop server up' `
                -Quiet)
        $detachedProbeHealthy =
            [bool]$serverProcessAlive -and
            -not $crashEvent -and
            $servingReached
        $outputWriter.WriteLine(
            "Detached probe: PID=$serverProcessId Alive=$([bool]$serverProcessAlive) " +
            "CrashEvent=$([bool]$crashEvent) Serving=$servingReached " +
            "Elapsed=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1))")
        $outputWriter.Flush()
    }
    elseif ($TickProbe) {
        $serverArguments = @(
            'TaleWorlds.Starter.DotNetCore.dll',
            $moduleToken,
            '/dedicatedcustomserver',
            $ServerPort.ToString([Globalization.CultureInfo]::InvariantCulture),
            'EU',
            '0')
        $serverArgumentLine = ($serverArguments |
            ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $dotnet
        $startInfo.Arguments = $serverArgumentLine
        $startInfo.WorkingDirectory = $serverBin
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $serverProcess = [Diagnostics.Process]::new()
        $serverProcess.StartInfo = $startInfo
        if (-not $serverProcess.Start()) {
            throw 'Could not start EOE tick-probe server process.'
        }
        $tickStdoutLog = $OutputLog + '.tick.stdout.txt'
        $tickStderrLog = $OutputLog + '.tick.stderr.txt'
        $stdoutStream = [IO.FileStream]::new(
            $tickStdoutLog,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::ReadWrite)
        $stderrStream = [IO.FileStream]::new(
            $tickStderrLog,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::ReadWrite)
        $stdoutTask = $serverProcess.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
        $stderrTask = $serverProcess.StandardError.BaseStream.CopyToAsync($stderrStream)
        $commandsSent = $false
        $saveSent = $false
        $commandSentAt = [TimeSpan]::Zero
        try {
            while (-not $serverProcess.HasExited -and
                   $stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
                $servingNow =
                    (Test-Path -LiteralPath $tickStdoutLog) -and
                    [bool](Select-String `
                        -LiteralPath $tickStdoutLog `
                        -Pattern 'SERVING|phase serving|coop server up' `
                        -Quiet)
                if ($servingNow -and -not $commandsSent) {
                    foreach ($command in @(
                        'coop.debug.get_time_mode',
                        'coop.debug.advance_time 1',
                        'coop.debug.set_time_mode Play_1x') + $ProbeCommands) {
                        $serverProcess.StandardInput.WriteLine($command)
                        $serverProcess.StandardInput.Flush()
                        Start-Sleep -Milliseconds 750
                    }
                    $commandsSent = $true
                    $commandSentAt = $stopwatch.Elapsed
                }
                if ($commandsSent -and -not $saveSent -and
                    ($stopwatch.Elapsed - $commandSentAt).TotalSeconds -ge 15) {
                    $serverProcess.StandardInput.WriteLine('save')
                    $serverProcess.StandardInput.Flush()
                    $saveSent = $true
                }
                Start-Sleep -Milliseconds 250
            }
            if (-not $serverProcess.HasExited) {
                $serverProcess.StandardInput.WriteLine('coop.debug.get_time_mode')
                $serverProcess.StandardInput.WriteLine('stop')
                $serverProcess.StandardInput.Flush()
                if (-not $serverProcess.WaitForExit(15000)) {
                    $serverProcess.Kill()
                    [void]$serverProcess.WaitForExit(10000)
                }
            }
            else {
                $serverProcess.WaitForExit()
            }
            $serverExitCode = $serverProcess.ExitCode
            [void]$stdoutTask.GetAwaiter().GetResult()
            [void]$stderrTask.GetAwaiter().GetResult()
            $stdoutStream.Flush()
            $stderrStream.Flush()
            $stdoutStream.Dispose()
            $stderrStream.Dispose()
            $stdoutStream = $null
            $stderrStream = $null
            $capturedOutput = [IO.File]::ReadAllText($tickStdoutLog)
            $capturedError = [IO.File]::ReadAllText($tickStderrLog)
            foreach ($line in (($capturedOutput + "`n" + $capturedError) -split "`r?`n")) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }
                $outputWriter.WriteLine($line)
                if ($line -match 'MissingMethodException|CSharpCompilationOptions') {
                    $roslynMismatch = $true
                }
                if ($line -match $signalPattern -and $line -notmatch '"ev":"commands"') {
                    $line
                }
            }
        }
        finally {
            $stopwatch.Stop()
            $outputWriter.Flush()
            if ($stdoutStream) {
                $stdoutStream.Dispose()
            }
            if ($stderrStream) {
                $stderrStream.Dispose()
            }
            $serverProcess.Dispose()
        }
    }
    else {
        Push-Location $serverBin
        try {
            & $dotnet `
                'TaleWorlds.Starter.DotNetCore.dll' `
                $moduleToken `
                '/dedicatedcustomserver' `
                $ServerPort.ToString([Globalization.CultureInfo]::InvariantCulture) `
                'EU' `
                '0' 2>&1 |
                ForEach-Object {
                    $line = $_.ToString()
                    $outputWriter.WriteLine($line)
                    if ($line -match 'MissingMethodException|CSharpCompilationOptions') {
                        $roslynMismatch = $true
                    }
                    if ($line -match $signalPattern) {
                        $line
                    }
                }
            $serverExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $stopwatch.Stop()
            $outputWriter.Flush()
        }
    }
    'SIGNALS_END'
    if (-not $DetachedProbe) {
        $servingReached = [bool](Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'SERVING|phase serving|coop server up' `
            -Quiet)
    }
    $freshSaveCompleted = $true
    if ($FreshSave) {
        $saveArtifactStates = @($smokeSavePaths | ForEach-Object {
            $item = Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue
            [pscustomobject]@{
                Path = $_
                Exists = [bool]$item
                Length = if ($item) { $item.Length } else { 0 }
            }
        })
        $saveCompletionTraced =
            (Test-Path -LiteralPath $bridgeTraceLog) -and
            [bool](Select-String `
                -LiteralPath $bridgeTraceLog `
                -Pattern 'Save completion (callback|observer):.*result=Success' `
                -Quiet)
        $freshSaveCompleted =
            -not ($saveArtifactStates | Where-Object { -not $_.Exists -or $_.Length -le 0 }) -and
            $saveCompletionTraced
        foreach ($saveArtifactState in $saveArtifactStates) {
            "SAVE_ARTIFACT=$($saveArtifactState.Path)|EXISTS=$($saveArtifactState.Exists)|BYTES=$($saveArtifactState.Length)"
        }
        "SAVE_COMPLETION_TRACED=$saveCompletionTraced FRESH_SAVE_COMPLETED=$freshSaveCompleted"
    }
    $existingSaveLoaded = $true
    if ($ExistingSaveName) {
        $campaignLoaded = [bool](Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'CAMPAIGN LOADED|Loaded save' `
            -Quiet)
        $seedPathUsed =
            (Test-Path -LiteralPath $bridgeTraceLog) -and
            [bool](Select-String `
                -LiteralPath $bridgeTraceLog `
                -Pattern 'Creating module-aware campaign' `
                -Quiet)
        $existingSaveLoaded = $campaignLoaded -and -not $seedPathUsed
        "EXISTING_SAVE=$ExistingSaveName CAMPAIGN_LOADED=$campaignLoaded SEED_PATH_USED=$seedPathUsed EXISTING_SAVE_LOADED=$existingSaveLoaded"
    }
    $tickProbePassed = $true
    if ($TickProbe) {
        $advanceAcknowledged =
            (Test-Path -LiteralPath $OutputLog) -and
            [bool](Select-String `
                -LiteralPath $OutputLog `
                -Pattern 'Advanced campaign time forward by 1 day' `
                -Quiet)
        $timeModeAcknowledged =
            (Test-Path -LiteralPath $OutputLog) -and
            [bool](Select-String `
                -LiteralPath $OutputLog `
                -Pattern 'Time control set to (Play_1x|Pause)' `
                -Quiet)
        $tickProbeCurrentSaveHash = Get-Sha256Hex ([IO.File]::ReadAllBytes(
            (Join-Path $smokeSaveRoot ($ExistingSaveName + '.sav'))))
        $saveChanged = $tickProbeCurrentSaveHash -ne $tickProbeOriginalSaveHash
        $tickProbePassed = $advanceAcknowledged -and $timeModeAcknowledged -and $saveChanged
        "TICK_ADVANCE_ACKNOWLEDGED=$advanceAcknowledged TIME_MODE_ACKNOWLEDGED=$timeModeAcknowledged SAVE_CHANGED=$saveChanged TICK_PROBE_PASSED=$tickProbePassed"
    }
    $terrainMarkerCount = @(
        Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'Installed pinned server map terrain size 1696x1696' `
            -SimpleMatch).Count
    $workshopEmptyCount = @(
        Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'Workshop produces empty items' `
            -SimpleMatch).Count
    $weatherFailureCount = @(
        Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'GetWeatherEventInPosition|DefaultMapWeatherModel|IndexOutOfRangeException').Count
    $bearskinCapeCount = @(
        Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'Bearskin does not fit to slot Cape' `
            -SimpleMatch).Count
    $legacyCivilianCount = @(
        Select-String `
            -LiteralPath $OutputLog `
            -Pattern 'This civilian tag should not be used anymore' `
            -SimpleMatch).Count
    $eoeCompatibilityPassed =
        $VanillaBaseline -or
        ($terrainMarkerCount -ge 1 -and
         $workshopEmptyCount -eq 0 -and
         $weatherFailureCount -eq 0 -and
         $bearskinCapeCount -eq 0 -and
         $legacyCivilianCount -eq 0)
    "EOE_TERRAIN_MARKER_COUNT=$terrainMarkerCount WORKSHOP_EMPTY_COUNT=$workshopEmptyCount WEATHER_FAILURE_COUNT=$weatherFailureCount BEARSKIN_CAPE_COUNT=$bearskinCapeCount LEGACY_CIVILIAN_COUNT=$legacyCivilianCount EOE_COMPATIBILITY_PASSED=$eoeCompatibilityPassed"
    $survived =
        $stopwatch.Elapsed.TotalSeconds -ge ($DurationSeconds - 5) -and
        $servingReached -and
        $freshSaveCompleted -and
        $existingSaveLoaded -and
        $tickProbePassed -and
        $eoeCompatibilityPassed -and
        (-not $DetachedProbe -or $detachedProbeHealthy)
    "EXIT=$serverExitCode ELAPSED_SECONDS=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) SERVING=$servingReached SURVIVED_${DurationSeconds}_SECONDS=$survived"

    if ($roslynMismatch) {
        throw 'Roslyn dependency mismatch remains.'
    }
    if (-not $survived) {
        'FAILURE_CONTEXT_BEGIN'
        $failureLogs = @($OutputLog, $engineOutputLog) |
            Where-Object { Test-Path -LiteralPath $_ }
        Select-String `
            -LiteralPath $failureLogs `
            -Pattern 'Unhandled|Exception|ERROR|FATAL|crash' `
            -Context 6, 20 |
            Select-Object -Last 5 |
            ForEach-Object { $_.ToString() }
        Get-Content -LiteralPath $OutputLog -Tail 80
        if (Test-Path -LiteralPath $engineOutputLog) {
            'ENGINE_OUTPUT_TAIL_BEGIN'
            Get-Content -LiteralPath $engineOutputLog -Tail 120
            'ENGINE_OUTPUT_TAIL_END'
        }
        if (Test-Path -LiteralPath $crashLog) {
            'MANAGED_CRASH_BEGIN'
            Get-Content -LiteralPath $crashLog
            'MANAGED_CRASH_END'
        }
        if ($bridgeTraceLog -and (Test-Path -LiteralPath $bridgeTraceLog)) {
            'BRIDGE_TRACE_BEGIN'
            Get-Content -LiteralPath $bridgeTraceLog
            'BRIDGE_TRACE_END'
        }
        'FAILURE_CONTEXT_END'
        if ($FreshSave -and -not $freshSaveCompleted) {
            throw 'EOE fresh-save smoke did not produce a successful non-empty save and sidecar.'
        }
        throw "EOE direct server exited before $DurationSeconds seconds."
    }

    "PASS: EOE direct server reached SERVING and stayed alive for $DurationSeconds seconds."
}
finally {
    if ($outputWriter) {
        $outputWriter.Dispose()
    }
    $remainingProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($ServerRoot, [StringComparison]::OrdinalIgnoreCase)
    }
    foreach ($remainingProcess in $remainingProcesses) {
        Stop-Process -Id $remainingProcess.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($TickProbe) {
        $tickProbeSaveRestored = $true
        foreach ($entry in $tickProbeOriginalSaveBytes.GetEnumerator()) {
            [IO.File]::WriteAllBytes($entry.Key, $entry.Value)
            $restoredSaveBytes = [IO.File]::ReadAllBytes($entry.Key)
            if ((Get-Sha256Hex $entry.Value) -ne (Get-Sha256Hex $restoredSaveBytes)) {
                $tickProbeSaveRestored = $false
            }
        }
        "TICK_PROBE_SAVE_RESTORED=$tickProbeSaveRestored"
        if (-not $tickProbeSaveRestored) {
            throw 'Tick probe did not restore the existing save byte-for-byte.'
        }
    }
    foreach ($smokeSavePath in $(if ($KeepFreshSave) { @() } else { $smokeSavePaths })) {
        $canonicalSmokeSavePath = [IO.Path]::GetFullPath($smokeSavePath)
        $canonicalSmokeSaveRoot = [IO.Path]::GetFullPath($smokeSaveRoot).TrimEnd('\') + '\'
        if (-not $canonicalSmokeSavePath.StartsWith(
                $canonicalSmokeSaveRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Smoke save path escaped the dedicated-server save directory: $canonicalSmokeSavePath"
        }
        if (Test-Path -LiteralPath $canonicalSmokeSavePath) {
            Remove-Item -LiteralPath $canonicalSmokeSavePath -Force
        }
    }
    if ($KeepFreshSave -and $smokeSaveName) {
        "SMOKE_SAVE_RETAINED=$smokeSaveName"
    }
    if ($watchdogJob) {
        Stop-Job -Job $watchdogJob -ErrorAction SilentlyContinue
        Remove-Job -Job $watchdogJob -Force -ErrorAction SilentlyContinue
    }
    [IO.File]::WriteAllBytes($config, $originalConfig)
    if ($originalManifest) {
        [IO.File]::WriteAllBytes($eoeManifest, $originalManifest)
        $restoredManifest = [IO.File]::ReadAllBytes($eoeManifest)
        "MANIFEST_RESTORED=$((Get-Sha256Hex $originalManifest) -eq (Get-Sha256Hex $restoredManifest))"
    }

    if ($previousEnvironment) {
        foreach ($entry in $previousEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
        }
    }

    $restoredConfig = [IO.File]::ReadAllBytes($config)
    $originalHash = Get-Sha256Hex $originalConfig
    $restoredHash = Get-Sha256Hex $restoredConfig
    "CONFIG_RESTORED=$($originalHash -eq $restoredHash)"

}
