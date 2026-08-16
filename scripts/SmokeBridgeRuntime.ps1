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
$modules = Join-Path $smokeRoot 'engine\Modules'
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

function Get-BridgeModuleId(
    [byte[]] $ConfigurationBytes,
    [byte[]] $ServerAssemblyBytes,
    [byte[]] $ClientAssemblyBytes,
    [byte[]] $BattleSceneCatalogContractBytes = $null) {
    $identityPayload = New-Object System.IO.MemoryStream
    try {
        $identityPayload.Write($ConfigurationBytes, 0, $ConfigurationBytes.Length)
        if ($null -ne $BattleSceneCatalogContractBytes) {
            $identityPayload.Write(
                $BattleSceneCatalogContractBytes,
                0,
                $BattleSceneCatalogContractBytes.Length)
        }
        $identityPayload.Write($ServerAssemblyBytes, 0, $ServerAssemblyBytes.Length)
        $identityPayload.Write($ClientAssemblyBytes, 0, $ClientAssemblyBytes.Length)
        $identityBytes = $identityPayload.ToArray()
    }
    finally {
        $identityPayload.Dispose()
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $configurationHash = ([BitConverter]::ToString(
            $sha.ComputeHash($identityBytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
    return 'BCS.CoopBridge.' + $configurationHash.Substring(0, 24).ToLowerInvariant()
}

try {
    [System.IO.Directory]::CreateDirectory($content) | Out-Null
    [System.IO.Directory]::CreateDirectory($contentBin) | Out-Null
    $native = Join-Path $modules 'Native'
    [System.IO.Directory]::CreateDirectory($native) | Out-Null
    $nativeServerBin = Join-Path $native 'bin\Win64_Shipping_Server'
    $nativeClientBin = Join-Path $native 'bin\Win64_Shipping_Client'
    [System.IO.Directory]::CreateDirectory($nativeServerBin) | Out-Null
    [System.IO.Directory]::CreateDirectory($nativeClientBin) | Out-Null
    $nativeActiveSubModule = '<SubModule><Name value="NativeSmoke"/><DLLName value="NativeSmoke.dll"/><SubModuleClassType value="NativeSmoke.SubModule"/></SubModule>'
    $nativeClientOnlySubModule = '<SubModule><Name value="ClientOnly"/><DLLName value="ClientOnly.dll"/><SubModuleClassType value="ClientOnly.SubModule"/></SubModule>'
    $nativeClientOnlySuppressedSubModule = '<SubModule><Name value="ClientOnly"/><DLLName value="ClientOnly.dll"/><SubModuleClassType value="ClientOnly.SubModule"/><Tags><Tag key="DedicatedServerType" value="none"/><Tag key="IsNoRenderModeElement" value="false"/></Tags></SubModule>'
    $nativeDisabledSubModule = '<SubModule><Name value="Disabled"/><DLLName value="Disabled.dll"/><SubModuleClassType value="Disabled.SubModule"/><Tags><Tag key="DedicatedServerType" value="none"/><Tag key="IsNoRenderModeElement" value="false"/></Tags></SubModule>'
    $nativeManifestPrefix = '<?xml version="1.0" encoding="utf-8"?><Module><Name value="Native"/><Id value="Native"/><Version value="v1.4.8"/><SingleplayerModule value="true"/><MultiplayerModule value="false"/><DependedModules/><SubModules>'
    $nativeManifestSuffix = '</SubModules></Module>'
    $serverNativeManifest = $nativeManifestPrefix + $nativeActiveSubModule + $nativeClientOnlySuppressedSubModule + $nativeDisabledSubModule + $nativeManifestSuffix
    $serverNativeClientOnlyActiveManifest = $nativeManifestPrefix + $nativeActiveSubModule + $nativeClientOnlySubModule + $nativeDisabledSubModule + $nativeManifestSuffix
    $clientNativeManifest = $nativeManifestPrefix + $nativeActiveSubModule + $nativeClientOnlySubModule + $nativeManifestSuffix
    $clientNativeDisabledManifest = $nativeManifestPrefix + $nativeActiveSubModule + $nativeClientOnlySubModule + $nativeDisabledSubModule + $nativeManifestSuffix
    $clientNativeExtraManifest = $nativeManifestPrefix + $nativeActiveSubModule + $nativeClientOnlySubModule + '<SubModule><Name value="Extra"/><DLLName value="Extra.dll"/><SubModuleClassType value="Extra.SubModule"/></SubModule>' + $nativeManifestSuffix
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
    $nativeId64 = [Convert]::ToBase64String($utf8.GetBytes('Native'))
    $nativeVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8'))
    $europe1700Id64 = [Convert]::ToBase64String($utf8.GetBytes('Europe1700'))
    $europe1700Version64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.7.1'))
    $sandboxCoreId64 = [Convert]::ToBase64String($utf8.GetBytes('SandBoxCore'))
    $sandboxCoreVersion64 = [Convert]::ToBase64String($utf8.GetBytes('v1.4.8'))
    $nativeSmokeDll64 = [Convert]::ToBase64String($utf8.GetBytes('NativeSmoke.dll'))
    $clientOnlyDll64 = [Convert]::ToBase64String($utf8.GetBytes('ClientOnly.dll'))
    $disabledDll64 = [Convert]::ToBase64String($utf8.GetBytes('Disabled.dll'))
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
    $nativeSmokeClientPath = Join-Path $nativeClientBin 'NativeSmoke.dll'
    & $frameworkCompiler /nologo /target:library /optimize+ /out:$nativeSmokeClientPath (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\AuthoritySmokeFixture.cs')
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Copy-Item -LiteralPath $nativeSmokeClientPath `
        -Destination (Join-Path $nativeServerBin 'NativeSmoke.dll')
    $clientOnlyClientPath = Join-Path $nativeClientBin 'ClientOnly.dll'
    & $frameworkCompiler /nologo /target:library /optimize+ /out:$clientOnlyClientPath (Join-Path $WorkspaceRoot 'BCSTool.CoopBridgeArtifact\AuthoritySmokeFixture.cs')
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
    $runtimeFeatures = @(
        'ClientMapEventCompatibility',
        'ClientRegistryLifecycleCompatibility',
        'ClientMapEventPositionAuthority',
        'ClientTroopUpgradeLoadRepair',
        'ClientSetDisorganizedDiagnostic',
        'ClientTroopRosterSequenceDiagnostic',
        'ClientCharacterCreationLifecycleCompatibility',
        'ServerRegistryLifecycleCompatibility',
        'ServerPopulationControl',
        'ServerFailedIdCompatibility',
        'ServerEurope1700ShieldProductionSuppression'
    )
    $deterministicBattleSceneFeature =
        'ClientDeterministicBattleSceneProjection'
    $schema3RuntimeFeatures = @(
        $runtimeFeatures + $deterministicBattleSceneFeature)
    $runtimeFeatureText = (($runtimeFeatures | Sort-Object | ForEach-Object {
        'RUNTIME_FEATURE|' + [Convert]::ToBase64String($utf8.GetBytes($_))
    }) -join [char]10) + [char]10
    $gameVersionCompatibilityRecord =
        'GAME_VERSION_COMPAT|' + $serverGameVersion64 + '|' +
        $clientGameVersion64 + '|' + $serverRuntimeVersion64 + '|' +
        $clientRuntimeVersion64 + [char]10
    $coopModuleRecords =
        'MODULE|' + $id64 + '|' + $version64 + '|' + $dll64 + '|' + [char]10 +
        'MODULE|' + $id64 + '|' + $version64 + '|' + $gameInterfaceDll64 + '|' + [char]10
    $nativeModuleRecords =
        'MODULE|' + $nativeId64 + '|' + $nativeVersion64 + '|' +
        $nativeSmokeDll64 + '|' + [char]10 +
        'MODULE|' + $nativeId64 + '|' + $nativeVersion64 + '|' +
        $clientOnlyDll64 + '|CLIENT_ONLY' + [char]10
    $configPolicyRecords =
        'DISABLED_SUBMODULE|' + $nativeId64 + '|' + $disabledDll64 + [char]10 +
        'IGNORE_CONTENT|' + $id64 + '|' + $visualPath64 + [char]10 +
        'CONTENT|' + $id64 + '|' + $contentHash + [char]10 +
        'AUTHORITY|' + $id64 + '|' + $dll64 + '|' + $type64 + '|' + $method64 + '|0|SERVER_ONLY' + [char]10 +
        'AUTHORITY|' + $id64 + '|' + $dll64 + '|' + $type64 + '|' + $clientMethod64 + '|0|CLIENT_ONLY' + [char]10
    $configText = 'BCS-COOP-BRIDGE|2' + [char]10 +
        $runtimeFeatureText +
        $gameVersionCompatibilityRecord +
        $coopModuleRecords +
        $nativeModuleRecords +
        $configPolicyRecords
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
    $bridgeManifest = '<?xml version="1.0" encoding="utf-8"?><Module><Name value="BCS Coop Bridge"/><Id value="' + $bridgeId + '"/><Version value="v0.6.73"/><SingleplayerModule value="true"/><MultiplayerModule value="false"/><DependedModules><DependedModule Id="Coop" DependentVersion="v1.0.0" Optional="false"/></DependedModules><ModuleType value="Community"/><SubModules><SubModule><Name value="BCS Coop Bridge"/><DLLName value="BCS.CoopBridge.dll"/><SubModuleClassType value="BCS.CoopBridge.BridgeSubModule"/></SubModule></SubModules><Xmls/></Module>'
    [System.IO.File]::WriteAllText((Join-Path $bridgeRoot 'SubModule.xml'), $bridgeManifest, $utf8)
    [System.IO.File]::WriteAllBytes((Join-Path $bridgeRoot 'bcs-coop-bridge.config'), $configBytes)
    Copy-Item -LiteralPath $serverBridgeAssemblySource -Destination (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll')
    Copy-Item -LiteralPath $clientBridgeAssemblySource -Destination (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll')

    $populationSettingsPath = Join-Path $smokeRoot 'bcs-coop-bridge-population.config'
    $economySettingsPath = Join-Path $smokeRoot 'bcs-coop-bridge-economy.config'
    $populationSettingsText = 'BCS-BRIDGE-POPULATION|3' + [char]10 +
        'MAXIMUM_AUTOMATIC_CARAVANS|236' + [char]10 +
        'AUTOMATIC_NPC_CARAVANS_PER_TOWN|1' + [char]10 +
        'MAXIMUM_ACTIVE_VILLAGER_PARTIES|400' + [char]10 +
        'BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|1' + [char]10 +
        'PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|0.5' + [char]10 +
        'REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|TRUE' + [char]10 +
        'REGIONAL_VILLAGER_TRADE_ENABLED|TRUE' + [char]10 +
        'REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED|TRUE' + [char]10 +
        'REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|TRUE' + [char]10
    $economySettingsText = 'BCS-BRIDGE-ECONOMY|1' + [char]10 +
        'CARAVAN_CAPACITY_MULTIPLIER|2' + [char]10 +
        'CARAVAN_TRADE_BUDGET_MULTIPLIER|2' + [char]10 +
        'CARAVAN_DESTINATION_AGE_MAX_BONUS|1' + [char]10 +
        'CARAVAN_DESTINATION_AGE_HORIZON_DAYS|30' + [char]10 +
        'VILLAGER_PARTY_CAPACITY_MULTIPLIER|1' + [char]10 +
        'VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|TRUE' + [char]10 +
        'VIRTUAL_VILLAGER_CARGO_MULTIPLIER|1' + [char]10 +
        'VIRTUAL_VILLAGER_COOLDOWN_DAYS|7' + [char]10 +
        'VIRTUAL_VILLAGER_TRAVEL_TIME_MULTIPLIER|1' + [char]10
    [System.IO.File]::WriteAllText($populationSettingsPath, $populationSettingsText, $utf8)
    [System.IO.File]::WriteAllText($economySettingsPath, $economySettingsText, $utf8)

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

    # Exercise the production parser, package identity, and pinned contract
    # loader with a real schema 3 package before the direct installed-hook ABI
    # probe seeds any runtime state.
    $battleSceneCatalogContractRelativePath =
        'BattleSceneCatalog/europe-1700-1.4.7.1-sandboxcore-1.4.8.bcs'
    $battleSceneCatalogContractSource = Join-Path $WorkspaceRoot `
        'BCSTool\Assets\BattleSceneCatalog\europe-1700-1.4.7.1-sandboxcore-1.4.8.bcs'
    $battleSceneCatalogContractBytes =
        [System.IO.File]::ReadAllBytes($battleSceneCatalogContractSource)
    $battleSceneCatalogSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $battleSceneCatalogContractHash = ([BitConverter]::ToString(
            $battleSceneCatalogSha.ComputeHash(
                $battleSceneCatalogContractBytes))).Replace('-', '')
    }
    finally {
        $battleSceneCatalogSha.Dispose()
    }
    $battleSceneCatalogRelativePath64 = [Convert]::ToBase64String(
        $utf8.GetBytes($battleSceneCatalogContractRelativePath))
    $schema3RuntimeFeatureText = (($schema3RuntimeFeatures |
        Sort-Object | ForEach-Object {
            'RUNTIME_FEATURE|' +
                [Convert]::ToBase64String($utf8.GetBytes($_))
        }) -join [char]10) + [char]10
    $schema3ContractRecord =
        'BATTLE_SCENE_CATALOG_CONTRACT|' + $europe1700Id64 + '|' +
        $europe1700Version64 + '|' + $sandboxCoreId64 + '|' +
        $sandboxCoreVersion64 + '|' + $battleSceneCatalogRelativePath64 + '|' +
        $battleSceneCatalogContractHash + '|WARN_ONLY' + [char]10
    $schema3ConfigText = 'BCS-COOP-BRIDGE|3' + [char]10 +
        $schema3RuntimeFeatureText +
        $schema3ContractRecord +
        $gameVersionCompatibilityRecord +
        $coopModuleRecords +
        'MODULE|' + $europe1700Id64 + '|' + $europe1700Version64 + '||' + [char]10 +
        $nativeModuleRecords +
        'MODULE|' + $sandboxCoreId64 + '|' + $sandboxCoreVersion64 + '||' + [char]10 +
        $configPolicyRecords
    $schema3ConfigBytes = $utf8.GetBytes($schema3ConfigText)
    $schema3BridgeId = Get-BridgeModuleId `
        $schema3ConfigBytes `
        $serverBridgeAssemblyBytes `
        $clientBridgeAssemblyBytes `
        $battleSceneCatalogContractBytes
    $schema3BridgeManifest = $bridgeManifest.Replace(
        $bridgeId,
        $schema3BridgeId)

    $europe1700Root = Join-Path $modules 'Europe1700'
    $sandboxCoreRoot = Join-Path $modules 'SandBoxCore'
    [System.IO.Directory]::CreateDirectory($europe1700Root) | Out-Null
    [System.IO.Directory]::CreateDirectory($sandboxCoreRoot) | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $europe1700Root 'SubModule.xml'),
        '<?xml version="1.0" encoding="utf-8"?><Module><Name value="Empires of Europe 1700"/><Id value="Europe1700"/><Version value="v1.4.7.1"/><DependedModules/><SubModules/></Module>',
        $utf8)
    [System.IO.File]::WriteAllText(
        (Join-Path $sandboxCoreRoot 'SubModule.xml'),
        '<?xml version="1.0" encoding="utf-8"?><Module><Name value="SandBox Core"/><Id value="SandBoxCore"/><Version value="v1.4.8"/><DependedModules/><SubModules/></Module>',
        $utf8)
    $installedBattleSceneCatalogPath = Join-Path `
        $bridgeRoot `
        $battleSceneCatalogContractRelativePath
    [System.IO.Directory]::CreateDirectory(
        (Split-Path $installedBattleSceneCatalogPath)) | Out-Null
    [System.IO.File]::WriteAllBytes(
        $installedBattleSceneCatalogPath,
        $battleSceneCatalogContractBytes)

    try {
        [System.IO.File]::WriteAllText(
            (Join-Path $bridgeRoot 'SubModule.xml'),
            $schema3BridgeManifest,
            $utf8)
        [System.IO.File]::WriteAllBytes(
            (Join-Path $bridgeRoot 'bcs-coop-bridge.config'),
            $schema3ConfigBytes)

        $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
        $env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES =
            $schema3RuntimeFeatures -join '|'
        try {
            $schema3ValidationOutput = @(
                & dotnet $serverHostPath `
                    (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') `
                    $serverHarmonyBin `
                    $serverGameBin `
                    $serverCoopBin 2>&1)
            $schema3ValidationExitCode = $LASTEXITCODE
            $schema3ValidationOutput | ForEach-Object { Write-Output $_ }
            if ($schema3ValidationExitCode -ne 0) {
                throw 'Server bridge runtime rejected the production-path schema 3 battle-scene contract.'
            }
            $schema3ValidationMarker =
                '[BCS Coop Bridge] Battle-scene catalog contract ' +
                $battleSceneCatalogContractHash + ' validated;'
            if (($schema3ValidationOutput -join [Environment]::NewLine).IndexOf(
                    $schema3ValidationMarker,
                    [StringComparison]::Ordinal) -lt 0) {
                throw 'Schema 3 smoke did not execute the pinned battle-scene contract loader.'
            }
            Write-Output 'PASS: production schema 3 validated its pinned battle-scene contract before the installed ABI smoke.'
        }
        finally {
            Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
            Remove-Item Env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES -ErrorAction SilentlyContinue
        }

        $env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_INSTALLED_ABI = '1'
        $env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA = '1'
        $env:BCS_BRIDGE_SMOKE_COOP_MODULE_ROOT = $CoopModuleRoot
        try {
            & $hostPath `
                (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') `
                $harmonyBin `
                $gameBin `
                $coopBin
            if ($LASTEXITCODE -ne 0) {
                throw 'Client bridge runtime failed the installed Coop battle-scene ABI smoke.'
            }
        }
        finally {
            Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_INSTALLED_ABI -ErrorAction SilentlyContinue
            Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA -ErrorAction SilentlyContinue
            Remove-Item Env:BCS_BRIDGE_SMOKE_COOP_MODULE_ROOT -ErrorAction SilentlyContinue
        }
    }
    finally {
        [System.IO.File]::WriteAllText(
            (Join-Path $bridgeRoot 'SubModule.xml'),
            $bridgeManifest,
            $utf8)
        [System.IO.File]::WriteAllBytes(
            (Join-Path $bridgeRoot 'bcs-coop-bridge.config'),
            $configBytes)
    }

    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
    $env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION = '1'
    $env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES = $runtimeFeatures -join '|'
    $env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS = '1'
    $env:BCS_BRIDGE_SMOKE_VALIDATE_REGIONAL_POPULATION_HOOKS = '1'
    $env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA = '1'
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_REGIONAL_POPULATION_HOOKS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA -ErrorAction SilentlyContinue
    }

    [System.IO.File]::WriteAllText(
        $economySettingsPath,
        $economySettingsText.Replace(
            'VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|TRUE',
            'VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|true'),
        $utf8)
    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
    $env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS = '1'
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -eq 0) {
            throw 'Server bridge runtime accepted a non-canonical economy boolean.'
        }
        Write-Output 'PASS: server bridge runtime rejected a malformed economy sidecar.'
    }
    finally {
        [System.IO.File]::WriteAllText($economySettingsPath, $economySettingsText, $utf8)
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS -ErrorAction SilentlyContinue
    }

    [System.IO.File]::WriteAllText(
        $nativeManifestPath,
        $serverNativeClientOnlyActiveManifest,
        $utf8)
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -eq 0) {
            throw 'Server bridge runtime accepted an active client-only submodule.'
        }
        Write-Output 'PASS: server bridge runtime rejected an active client-only submodule.'
    }
    finally {
        [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
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
    $env:BCS_BRIDGE_SMOKE_VALIDATE_CHARACTER_CREATION_GATE = '1'
    $env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_RANDOM_SCOPE = '1'
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
        [System.IO.File]::WriteAllText(
            $nativeManifestPath,
            $clientNativeDisabledManifest,
            $utf8)
        & $hostPath (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') $harmonyBin $gameBin $coopBin
        if ($LASTEXITCODE -eq 0) {
            throw 'Client bridge runtime accepted a disabled submodule declaration.'
        }
        Write-Output 'PASS: client bridge runtime rejected a disabled submodule declaration.'

        [System.IO.File]::WriteAllText(
            $nativeManifestPath,
            $clientNativeExtraManifest,
            $utf8)
        & $hostPath (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') $harmonyBin $gameBin $coopBin
        if ($LASTEXITCODE -eq 0) {
            throw 'Client bridge runtime accepted an unconfigured DLL declaration.'
        }
        Write-Output 'PASS: client bridge runtime rejected an unconfigured DLL declaration.'
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_ACTIVE_IDS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_ACTIVE_PATHS -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_OVERRIDE_ACTIVE_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_WORKING_DIRECTORY -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_CHARACTER_CREATION_GATE -ErrorAction SilentlyContinue
        Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_RANDOM_SCOPE -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientGuiXmlPath -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $clientNestedJsonPath -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText($externalVisualPath, $visualXml, $utf8)
        [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
        Move-Item -LiteralPath $externalContent -Destination $content
    }
    Write-Output 'PASS: client bridge discovered an active external Workshop-style module root.'

    # A schema 2 package without feature records is the generic baseline. It
    # must validate without silently activating compatibility written for EOE.
    $genericConfigLines = $configText.Split(
        [string[]] @("`r`n", "`n"),
        [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { -not $_.StartsWith('RUNTIME_FEATURE|', [StringComparison]::Ordinal) }
    $genericConfigText = ($genericConfigLines -join [char]10) + [char]10
    $genericConfigBytes = $utf8.GetBytes($genericConfigText)
    $genericBridgeId = Get-BridgeModuleId `
        $genericConfigBytes `
        $serverBridgeAssemblyBytes `
        $clientBridgeAssemblyBytes
    $genericBridgeManifest = $bridgeManifest.Replace($bridgeId, $genericBridgeId)
    [System.IO.File]::WriteAllText(
        (Join-Path $bridgeRoot 'SubModule.xml'),
        $genericBridgeManifest,
        $utf8)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $bridgeRoot 'bcs-coop-bridge.config'),
        $genericConfigBytes)
    $bridgeId = $genericBridgeId
    $bridgeManifest = $genericBridgeManifest
    Remove-Item Env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES -ErrorAction SilentlyContinue
    $env:BCS_BRIDGE_SMOKE_DISABLED_FEATURES =
        $schema3RuntimeFeatures -join '|'
    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Generic server bridge runtime activated a target-specific compatibility feature.'
        }
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllText($nativeManifestPath, $clientNativeManifest, $utf8)
    try {
        & $hostPath (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') $harmonyBin $gameBin $coopBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Generic client bridge runtime activated a target-specific compatibility feature.'
        }
    }
    finally {
        [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
    }
    Remove-Item Env:BCS_BRIDGE_SMOKE_DISABLED_FEATURES -ErrorAction SilentlyContinue
    $env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES = $runtimeFeatures -join '|'
    Write-Output 'PASS: generic schema 2 kept all target-specific runtime features disabled.'

    # Schema 1 packages predate explicit feature records. Re-identify the same
    # package with a legacy configuration and prove that every historical hook
    # remains enabled on both runtime roles.
    $legacyConfigLines = $configText.Split(
        [string[]] @("`r`n", "`n"),
        [System.StringSplitOptions]::RemoveEmptyEntries) |
        Select-Object -Skip 1 |
        Where-Object {
            -not $_.StartsWith('RUNTIME_FEATURE|', [StringComparison]::Ordinal) -and
            -not $_.StartsWith('DISABLED_SUBMODULE|', [StringComparison]::Ordinal) -and
            -not ($_.StartsWith('MODULE|', [StringComparison]::Ordinal) -and
                  $_.EndsWith('|CLIENT_ONLY', [StringComparison]::Ordinal))
        }
    $legacyConfigText = 'BCS-COOP-BRIDGE|1' + [char]10 +
        ($legacyConfigLines -join [char]10) + [char]10
    $legacyConfigBytes = $utf8.GetBytes($legacyConfigText)
    $legacyBridgeId = Get-BridgeModuleId `
        $legacyConfigBytes `
        $serverBridgeAssemblyBytes `
        $clientBridgeAssemblyBytes
    $legacyBridgeManifest = $bridgeManifest.Replace($bridgeId, $legacyBridgeId)
    [System.IO.File]::WriteAllText(
        (Join-Path $bridgeRoot 'SubModule.xml'),
        $legacyBridgeManifest,
        $utf8)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $bridgeRoot 'bcs-coop-bridge.config'),
        $legacyConfigBytes)
    $bridgeId = $legacyBridgeId
    $bridgeManifest = $legacyBridgeManifest

    # Schema 1 predates deterministic battle-scene projection. It retains only
    # its historical feature set; newly added features must remain disabled.
    $env:BCS_BRIDGE_SMOKE_DISABLED_FEATURES = @(
        $deterministicBattleSceneFeature,
        'ServerEurope1700ShieldProductionSuppression'
    ) -join '|'
    $env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES = ($runtimeFeatures |
        Where-Object {
            $_ -ne $deterministicBattleSceneFeature -and
            $_ -ne 'ServerEurope1700ShieldProductionSuppression'
        }) -join '|'
    $env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY = $serverGameInterfaceAssembly
    try {
        & dotnet $serverHostPath (Join-Path $serverBridgeBin 'BCS.CoopBridge.dll') $serverHarmonyBin $serverGameBin $serverCoopBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Server bridge runtime did not preserve schema 1 compatibility features.'
        }
    }
    finally {
        Remove-Item Env:BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllText($nativeManifestPath, $clientNativeManifest, $utf8)
    try {
        & $hostPath (Join-Path $clientBridgeBin 'BCS.CoopBridge.dll') $harmonyBin $gameBin $coopBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Client bridge runtime did not preserve schema 1 compatibility features.'
        }
    }
    finally {
        [System.IO.File]::WriteAllText($nativeManifestPath, $serverNativeManifest, $utf8)
    }
    Remove-Item Env:BCS_BRIDGE_SMOKE_DISABLED_FEATURES -ErrorAction SilentlyContinue
    Write-Output 'PASS: legacy schema 1 preserved all historical runtime compatibility features.'

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
    Remove-Item Env:BCS_BRIDGE_SMOKE_EXPECTED_FEATURES -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_DISABLED_FEATURES -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_RANDOM_SCOPE -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_INSTALLED_ABI -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_COOP_MODULE_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_REGIONAL_POPULATION_HOOKS -ErrorAction SilentlyContinue
    Remove-Item Env:BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $smokeRoot) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}
