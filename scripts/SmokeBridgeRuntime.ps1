param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceRoot,
    [Parameter(Mandatory = $true)]
    [string] $BannerlordRoot,
    [Parameter(Mandatory = $true)]
    [string] $CoopModuleRoot
)

$ErrorActionPreference = 'Stop'
$smokeRoot = Join-Path $env:TEMP ('bcs-bridge-runtime-smoke-' + [guid]::NewGuid().ToString('N'))
$modules = Join-Path $smokeRoot 'Modules'
$content = Join-Path $modules 'ContentPack'
$contentBin = Join-Path $content 'bin\Win64_Shipping_Client'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$gameBin = Join-Path $BannerlordRoot 'bin\Win64_Shipping_Client'
$coopBin = Join-Path $CoopModuleRoot 'bin\Win64_Shipping_Client'
$harmonyBin = $coopBin
$serverGameBin = Join-Path $CoopModuleRoot 'DedicatedServer\engine\bin\Win64_Shipping_Server'
$serverCoopBin = Join-Path $CoopModuleRoot 'DedicatedServer\engine\Modules\Coop\bin\Win64_Shipping_Server'
$serverHarmonyBin = $serverCoopBin
$serverGameInterfaceAssembly = Join-Path $serverCoopBin 'GameInterface.dll'

try {
    [System.IO.Directory]::CreateDirectory($content) | Out-Null
    [System.IO.Directory]::CreateDirectory($contentBin) | Out-Null
    $native = Join-Path $modules 'Native'
    [System.IO.Directory]::CreateDirectory($native) | Out-Null
    $serverNativeManifest = '<?xml version="1.0" encoding="utf-8"?><Module><Name value="Native"/><Id value="Native"/><Version value="v1.4.8"/><SingleplayerModule value="true"/><MultiplayerModule value="false"/><DependedModules/><SubModules/></Module>'
    $clientNativeManifest = $serverNativeManifest
    $nativeManifestPath = Join-Path $native 'SubModule.xml'
    [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
    $contentManifest = '<?xml version="1.0" encoding="utf-8"?><Module><Name value="Coop"/><Id value="Coop"/><Version value="v1.0.0"/><SingleplayerModule value="true"/><MultiplayerModule value="false"/><DependedModules/><SubModules/></Module>'
    [System.IO.File]::WriteAllText((Join-Path $content 'SubModule.xml'), $contentManifest, $utf8)
    $moduleData = Join-Path $content 'ModuleData'
    [System.IO.Directory]::CreateDirectory($moduleData) | Out-Null
    $contentXml = '<Items><Item id="content_a" /></Items>'
    $contentXmlPath = Join-Path $moduleData 'content.xml'
    [System.IO.File]::WriteAllText($contentXmlPath, $contentXml, $utf8)
    $visualXmlPath = Join-Path $moduleData 'visual.xml'
    $visualXml = '<Visuals particle="client_only" />'
    [System.IO.File]::WriteAllText($visualXmlPath, $visualXml, $utf8)

    $id64 = [Convert]::ToBase64String($utf8.GetBytes('Coop'))
    $version64 = [Convert]::ToBase64String($utf8.GetBytes('v1.0.0'))
    $fileSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fileHash = $fileSha.ComputeHash([System.IO.File]::ReadAllBytes($contentXmlPath))
    }
    finally {
        $fileSha.Dispose()
    }
    $payload = New-Object System.IO.MemoryStream
    try {
        $relativeBytes = $utf8.GetBytes('ModuleData/content.xml')
        $payload.Write($relativeBytes, 0, $relativeBytes.Length)
        $payload.WriteByte(0)
        $payload.Write($fileHash, 0, $fileHash.Length)
        $contentSha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $contentHash = ([BitConverter]::ToString($contentSha.ComputeHash($payload.ToArray()))).Replace('-', '')
        }
        finally {
            $contentSha.Dispose()
        }
    }
    finally {
        $payload.Dispose()
    }
    $fixturePath = Join-Path $contentBin 'AuthoritySmokeFixture.dll'
    $frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $netStandardFacade = Join-Path $programFilesX86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll'

    & $frameworkCompiler /nologo /target:library /optimize+ /out:$fixturePath (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\AuthoritySmokeFixture.cs')
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $gameInterfaceFixturePath = Join-Path $contentBin 'GameInterface.dll'
    & $frameworkCompiler /nologo /target:library /optimize+ /out:$gameInterfaceFixturePath "/reference:$(Join-Path $gameBin 'TaleWorlds.Library.dll')" "/reference:$netStandardFacade" (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\GameVersionSmokeFixture.cs')
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Copy-Item -LiteralPath (Join-Path $coopBin 'Common.dll') `
        -Destination (Join-Path $contentBin 'Common.dll')
    $fixtureSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fixtureHash = ([BitConverter]::ToString($fixtureSha.ComputeHash([System.IO.File]::ReadAllBytes($fixturePath)))).Replace('-', '')
    }
    finally {
        $fixtureSha.Dispose()
    }
    $gameInterfaceFixtureSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $gameInterfaceFixtureHash = ([BitConverter]::ToString(
            $gameInterfaceFixtureSha.ComputeHash(
                [System.IO.File]::ReadAllBytes($gameInterfaceFixturePath)))).Replace('-', '')
    }
    finally {
        $gameInterfaceFixtureSha.Dispose()
    }
    $dll64 = [Convert]::ToBase64String($utf8.GetBytes('AuthoritySmokeFixture.dll'))
    $gameInterfaceDll64 = [Convert]::ToBase64String($utf8.GetBytes('GameInterface.dll'))
    $type64 = [Convert]::ToBase64String($utf8.GetBytes('AuthoritySmokeFixture.Target'))
    $method64 = [Convert]::ToBase64String($utf8.GetBytes('Mutate'))
    $clientMethod64 = [Convert]::ToBase64String($utf8.GetBytes('MutateClientOnly'))
    $visualPath64 = [Convert]::ToBase64String($utf8.GetBytes('ModuleData/visual.xml'))
    $serverGameVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8'))
    $clientGameVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8'))
    $serverRuntimeVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8.123456'))
    $clientRuntimeVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8.123457'))
    $configText = 'BCS-COOP-BRIDGE|1' + [char]10 +
        'GAME_VERSION_COMPAT|' + $serverGameVersion64 + '|' + $clientGameVersion64 + '|' + $serverRuntimeVersion64 + '|' + $clientRuntimeVersion64 + [char]10 +
        'MODULE|' + $id64 + '|' + $version64 + '|' + $dll64 + '|' + $fixtureHash + [char]10 +
        'MODULE|' + $id64 + '|' + $version64 + '|' + $gameInterfaceDll64 + '|' + $gameInterfaceFixtureHash + [char]10 +
        'IGNORE_CONTENT|' + $id64 + '|' + $visualPath64 + [char]10 +
        'CONTENT|' + $id64 + '|' + $contentHash + [char]10 +
        'AUTHORITY|' + $id64 + '|' + $dll64 + '|' + $type64 + '|' + $method64 + '|0|SERVER_ONLY' + [char]10 +
        'AUTHORITY|' + $id64 + '|' + $dll64 + '|' + $type64 + '|' + $clientMethod64 + '|0|CLIENT_ONLY' + [char]10
    $configBytes = $utf8.GetBytes($configText)
    $serverBridgeAssemblySource = Join-Path $WorkspaceRoot 'BCSTool\Assets\CoopBridge\BCS.CoopBridge.Server.dll'
    $clientBridgeAssemblySource = Join-Path $WorkspaceRoot 'BCSTool\Assets\CoopBridge\BCS.CoopBridge.Client.dll'
    $identityPayload = New-Object System.IO.MemoryStream
    try {
        $identityPayload.Write($configBytes, 0, $configBytes.Length)
        $serverBridgeAssemblyBytes = [System.IO.File]::ReadAllBytes($serverBridgeAssemblySource)
        $clientBridgeAssemblyBytes = [System.IO.File]::ReadAllBytes($clientBridgeAssemblySource)
        $identityPayload.Write($serverBridgeAssemblyBytes, 0, $serverBridgeAssemblyBytes.Length)
        $identityPayload.Write($clientBridgeAssemblyBytes, 0, $clientBridgeAssemblyBytes.Length)
        $identityBytes = $identityPayload.ToArray()
    }
    finally {
        $identityPayload.Dispose()
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $configHash = ([BitConverter]::ToString($sha.ComputeHash($identityBytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }

    $bridgeId = 'BCS.CoopBridge.' + $configHash.Substring(0, 24).ToLowerInvariant()
    $bridgeRoot = Join-Path $modules $bridgeId
    $serverBridgeBin = Join-Path $bridgeRoot 'bin\Win64_Shipping_Server'
    $clientBridgeBin = Join-Path $bridgeRoot 'bin\Win64_Shipping_Client'
    [System.IO.Directory]::CreateDirectory($serverBridgeBin) | Out-Null
    [System.IO.Directory]::CreateDirectory($clientBridgeBin) | Out-Null
    $bridgeManifest = '<?xml version="1.0" encoding="utf-8"?><Module><Name value="BCS Coop Bridge"/><Id value="' + $bridgeId + '"/><Version value="v0.6.64"/><SingleplayerModule value="true"/><MultiplayerModule value="false"/><DependedModules><DependedModule Id="Coop" DependentVersion="v1.0.0" Optional="false"/></DependedModules><ModuleType value="Community"/><SubModules><SubModule><Name value="BCS Coop Bridge"/><DLLName value="BCS.CoopBridge.dll"/><SubModuleClassType value="BCS.CoopBridge.BridgeSubModule"/></SubModule></SubModules><Xmls/></Module>'
    [System.IO.File]::WriteAllText((Join-Path $bridgeRoot 'SubModule.xml'), $bridgeManifest, $utf8)
    [System.IO.File]::WriteAllBytes((Join-Path $bridgeRoot 'bcs-coop-bridge.config'), $configBytes)
    Copy-Item -LiteralPath $serverBridgeAssemblySource -Destination (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll')
    Copy-Item -LiteralPath $clientBridgeAssemblySource -Destination (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll')

    $hostPath = Join-Path $WorkspaceRoot 'artifacts\BridgeSmokeHost.exe'
    [System.IO.Directory]::CreateDirectory((Split-Path $hostPath)) | Out-Null
    & $frameworkCompiler /nologo /target:exe /optimize+ /out:$hostPath (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\BridgeSmokeHost.cs')
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $serverHostProjectRoot = Join-Path $smokeRoot 'ServerBridgeSmokeHost'
    $serverHostOutput = Join-Path $serverHostProjectRoot 'out'
    [System.IO.Directory]::CreateDirectory($serverHostProjectRoot) | Out-Null
    $serverHostProjectPath = Join-Path $serverHostProjectRoot 'BridgeSmokeHost.Server.csproj'
    $serverHostSource = [Security.SecurityElement]::Escape(
        (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\BridgeSmokeHost.cs'))
    $serverHostProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <UseAppHost>false</UseAppHost>
    <AssemblyName>BridgeSmokeHost.Server</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$serverHostSource" Link="BridgeSmokeHost.cs" />
  </ItemGroup>
</Project>
"@
    [System.IO.File]::WriteAllText($serverHostProjectPath, $serverHostProject, $utf8)
    & dotnet build $serverHostProjectPath --nologo --configuration Release --verbosity quiet --output $serverHostOutput
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $serverHostPath = Join-Path $serverHostOutput 'BridgeSmokeHost.Server.dll'
    if (-not (Test-Path -LiteralPath $serverHostPath -PathType Leaf)) {
        throw 'The temporary net6 server bridge smoke host was not produced.'
    }

    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
    $env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION = '1'
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION -ErrorAction SilentlyContinue
    }

    $externalModules = Join-Path $smokeRoot 'WorkshopModules'
    [System.IO.Directory]::CreateDirectory($externalModules) | Out-Null
    $externalContent = Join-Path $externalModules 'ContentPack'
    Move-Item -LiteralPath $content -Destination $externalContent
    $moduleManagerAssembly = Join-Path $gameBin 'TaleWorlds.ModuleManager.dll'
    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $moduleManagerAssembly + [IO.Path]::PathSeparator + (Join-Path $externalContent 'bin\Win64_Shipping_Client\AuthoritySmokeFixture.dll') + [IO.Path]::PathSeparator + (Join-Path $externalContent 'bin\Win64_Shipping_Client\GameInterface.dll')
    $env:BCS_BRIDGE_SMOKE_ACTIVE_IDS = 'Coop'
    $env:BCS_BRIDGE_SMOKE_ACTIVE_PATHS = $externalContent
    $env:BCS_BRIDGE_SMOKE_OVERRIDE_ACTIVE_ROOT = '$BASE/Modules/ContentPack'
    $env:BCS_BRIDGE_SMOKE_WORKING_DIRECTORY = $gameBin
    $env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION = '1'
    [System.IO.File]::WriteAllText($nativeManifestPath, $clientNativeManifest, $utf8)
    $externalVisualPath = Join-Path $externalContent 'ModuleData\visual.xml'
    Remove-Item -LiteralPath $externalVisualPath
    $clientGuiXmlPath = Join-Path $externalContent 'GUI\Prefabs\client-only.xml'
    $clientNestedJsonPath = Join-Path $externalContent 'DedicatedServer\engine\nested-server-package.json'
    [System.IO.Directory]::CreateDirectory((Split-Path $clientGuiXmlPath)) | Out-Null
    [System.IO.Directory]::CreateDirectory((Split-Path $clientNestedJsonPath)) | Out-Null
    [System.IO.File]::WriteAllText($clientGuiXmlPath, '<Prefab />', $utf8)
    [System.IO.File]::WriteAllText($clientNestedJsonPath, '{}', $utf8)
    try {
        & $hostPath (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') $harmonyBin $gameBin $coopBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Client bridge runtime could not discover an active module outside the local Modules directory.'
        }
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_ACTIVE_IDS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_ACTIVE_PATHS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_OVERRIDE_ACTIVE_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_WORKING_DIRECTORY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientGuiXmlPath -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientNestedJsonPath -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText($externalVisualPath, $visualXml, $utf8)
        [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
        Move-Item -LiteralPath $externalContent -Destination $content
    }
    Write-Output 'PASS: client bridge discovered an active external Workshop-style module root.'

    [System.IO.File]::WriteAllText($visualXmlPath, '<Visuals particle="server_headless" />', $utf8)
    & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
    if ($LASTEXITCODE -ne 0) {
        throw 'Bridge runtime rejected an explicit server-only visual difference.'
    }
    Write-Output 'PASS: bridge runtime allowed the declared server-only visual difference.'
    [System.IO.File]::WriteAllText($visualXmlPath, $visualXml, $utf8)

    $fixtureBytes = [System.IO.File]::ReadAllBytes($fixturePath)
    try {
        [System.IO.File]::WriteAllBytes($fixturePath, [byte[]](0x42, 0x43, 0x53))
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -eq 0) {
            throw 'Bridge runtime accepted an unreadable required managed assembly.'
        }
        Write-Output 'PASS: bridge runtime rejected an unreadable required managed assembly.'
    }
    finally {
        [System.IO.File]::WriteAllBytes($fixturePath, $fixtureBytes)
    }

    $tamperedManifest = $contentManifest.Replace('v1.0.0', 'v1.0.1')
    [System.IO.File]::WriteAllText((Join-Path $content 'SubModule.xml'), $tamperedManifest, $utf8)
    & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
    if ($LASTEXITCODE -eq 0) {
        throw 'Bridge runtime accepted a tampered module version.'
    }
    Write-Output 'PASS: bridge runtime rejected a tampered module version.'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $smokeRoot) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}
