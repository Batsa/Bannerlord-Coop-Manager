param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,

    [string[]]$EnabledDlls = @(),

    [ValidateRange(10, 300)]
    [int]$Seconds = 15
)

$ErrorActionPreference = 'Stop'
$serverRootPath = [IO.Path]::GetFullPath($ServerRoot)
$moduleRoot = [IO.Path]::GetFullPath((Join-Path $serverRootPath 'engine\Modules\3231544373'))
$manifestPath = [IO.Path]::GetFullPath((Join-Path $moduleRoot 'SubModule.xml'))
if (-not $manifestPath.StartsWith(
        $moduleRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe EOE manifest path: $manifestPath"
}
if (-not [IO.File]::Exists($manifestPath)) {
    throw "EOE server manifest was not found: $manifestPath"
}

$knownDlls = @(
    'RF_BattleAI.dll',
    'XMLMeleePatch.dll',
    'BattleArtilleryReworked.dll',
    'Europe1700.dll',
    'Bannerlord.EOEPatches.dll',
    'ClansResourceAdder.dll',
    'CustomizableClanTier.dll'
)
foreach ($dll in $EnabledDlls) {
    if ($knownDlls -notcontains $dll) {
        throw "Unknown EOE diagnostic DLL: $dll"
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$harness = Join-Path $repoRoot 'BCSTool.RegressionTests\bin\Release\net10.0-windows\BCSTool.RegressionTests.dll'
$bootstrap = Join-Path $repoRoot 'BCSTool\bin\Release\net10.0-windows\BCSTool.RuntimeBootstrap.dll'
$serverExecutable = Join-Path $serverRootPath 'BannerlordCoopServer.exe'
foreach ($required in @($harness, $bootstrap, $serverExecutable)) {
    if (-not [IO.File]::Exists($required)) {
        throw "Required smoke-test artifact was not found: $required"
    }
}

$original = [IO.File]::ReadAllBytes($manifestPath)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $originalHash = (($sha.ComputeHash($original) | ForEach-Object { $_.ToString('X2') }) -join '')
} finally {
    $sha.Dispose()
}

$smokeOutput = @()
$smokeCode = 1
try {
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $input = [IO.MemoryStream]::new($original, $false)
    $readerSettings = [Xml.XmlReaderSettings]::new()
    $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($input, $readerSettings)
    try {
        $document.Load($reader)
    } finally {
        $reader.Dispose()
        $input.Dispose()
    }

    foreach ($submodule in @($document.SelectNodes('/Module/SubModules/SubModule'))) {
        $dllName = $submodule.SelectSingleNode('./DLLName').GetAttribute('value')
        $tags = $submodule.SelectSingleNode('./Tags')
        if ($null -eq $tags) {
            $tags = $document.CreateElement('Tags')
            $null = $submodule.AppendChild($tags)
        }
        foreach ($tag in @($tags.SelectNodes(
                    "./Tag[@key='DedicatedServerType' or @key='IsNoRenderModeElement']"))) {
            $null = $tags.RemoveChild($tag)
        }
        if ($EnabledDlls -notcontains $dllName) {
            $tag = $document.CreateElement('Tag')
            $tag.SetAttribute('key', 'DedicatedServerType')
            $tag.SetAttribute('value', 'none')
            $null = $tags.AppendChild($tag)
            $tag = $document.CreateElement('Tag')
            $tag.SetAttribute('key', 'IsNoRenderModeElement')
            $tag.SetAttribute('value', 'false')
            $null = $tags.AppendChild($tag)
        }
    }

    $prepared = [IO.MemoryStream]::new()
    $writerSettings = [Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [Text.UTF8Encoding]::new($false, $true)
    $writerSettings.Indent = $true
    $writerSettings.NewLineChars = [Environment]::NewLine
    $writerSettings.OmitXmlDeclaration = $true
    $writer = [Xml.XmlWriter]::Create($prepared, $writerSettings)
    try {
        $document.Save($writer)
    } finally {
        $writer.Dispose()
    }
    [IO.File]::WriteAllBytes($manifestPath, $prepared.ToArray())

    $env:WINDIR = $env:SystemRoot
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $smokeOutput = & dotnet $harness --server-smoke $serverExecutable $Seconds $bootstrap 2>&1
        $smokeCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
} finally {
    [IO.File]::WriteAllBytes($manifestPath, $original)
}

$sha = [Security.Cryptography.SHA256]::Create()
try {
    $restoredHash = (($sha.ComputeHash([IO.File]::ReadAllBytes($manifestPath)) |
            ForEach-Object { $_.ToString('X2') }) -join '')
} finally {
    $sha.Dispose()
}
if ($restoredHash -ne $originalHash) {
    throw "EOE diagnostic manifest did not restore exactly: $manifestPath"
}

"EOE_ENABLED_DLLS=$($EnabledDlls -join ',')"
'MANIFEST_RESTORED=True'
$smokeOutput |
    Select-String -Pattern 'PASS:|FAIL:|Exception|ERROR|Error|error|Warning|warning|OnBeforeInitialModule|BCS Coop Bridge|server|Command Args' |
    Select-Object -Last 160
"SMOKE_EXIT_CODE=$smokeCode"
exit $smokeCode
