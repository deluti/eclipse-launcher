param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.5.1',
    [string]$IsccPath,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$eclipseWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$eclipseRelease = Join-Path $eclipseWorkspace 'artifacts\release'
$eclipsePublish = Join-Path $eclipseRelease 'win-x86'
$env:DOTNET_CLI_HOME = Join-Path $eclipseWorkspace '.test-data\dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $IsccPath) {
    $eclipseCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $eclipseWorkspace '.test-data\release-tools\InnoSetup\ISCC.exe')
    )
    $IsccPath = $eclipseCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) { throw 'Install Inno Setup 6/7 or pass -IsccPath.' }
if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $eclipsePublish) {
        $eclipseResolved = [IO.Path]::GetFullPath($eclipsePublish)
        if ($eclipseResolved -ne (Join-Path $eclipseWorkspace 'artifacts\release\win-x86')) { throw 'Unsafe publish directory' }
        Remove-Item -LiteralPath $eclipseResolved -Recurse -Force
    }
    dotnet publish (Join-Path $eclipseWorkspace 'src\Eclipse.Launcher\Eclipse.Launcher.csproj') -c Release -r win-x86 --self-contained true -p:RuntimeFrameworkVersion=10.0.15 -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" --source https://api.nuget.org/v3/index.json -o $eclipsePublish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
}
if (-not (Test-Path -LiteralPath (Join-Path $eclipsePublish 'coreclr.dll'))) { throw 'Missing self-contained runtime' }
$eclipseRuntime = Get-Content -LiteralPath (Join-Path $eclipsePublish 'Eclipse.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $eclipseRuntime.runtimeOptions.includedFrameworks) { throw 'Framework-dependent builds cannot be packaged' }
$eclipseBytes = [IO.File]::ReadAllBytes((Join-Path $eclipsePublish 'Eclipse.exe'))
$eclipsePeOffset = [BitConverter]::ToInt32($eclipseBytes, 0x3c)
if ([BitConverter]::ToUInt16($eclipseBytes, $eclipsePeOffset + 4) -ne 0x14c) { throw 'Expected x86 launcher' }
if (Test-Path -LiteralPath (Join-Path $eclipsePublish 'data')) { throw 'Game data must never be included in a release' }
Copy-Item -LiteralPath (Join-Path $eclipseWorkspace 'README.md') -Destination $eclipsePublish -Force
Copy-Item -LiteralPath (Join-Path $eclipseWorkspace 'PERFORMANCE.md') -Destination $eclipsePublish -Force
if (Test-Path -LiteralPath (Join-Path $eclipseWorkspace 'README.ru.md')) { Copy-Item -LiteralPath (Join-Path $eclipseWorkspace 'README.ru.md') -Destination $eclipsePublish -Force }
$eclipseNotices = Join-Path $eclipsePublish 'Notices'
New-Item -ItemType Directory -Path $eclipseNotices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $eclipseWorkspace 'THIRD-PARTY.md') -Destination $eclipseNotices -Force
$eclipsePackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:DOTNET_CLI_HOME '.nuget\packages' }
Copy-Item -LiteralPath (Join-Path $eclipsePackages 'microsoft.netcore.app.runtime.win-x86\10.0.15\LICENSE.TXT') -Destination (Join-Path $eclipseNotices 'dotnet-runtime-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $eclipsePackages 'microsoft.netcore.app.runtime.win-x86\10.0.15\THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $eclipseNotices 'dotnet-THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath (Join-Path $eclipsePackages 'microsoft.windowsdesktop.app.runtime.win-x86\10.0.15\LICENSE') -Destination (Join-Path $eclipseNotices 'dotnet-windowsdesktop-LICENSE.txt') -Force
& $IsccPath '/Qp' "/DAppVersion=$Version" "/DPublishDir=$eclipsePublish" "/DReleaseDir=$eclipseRelease" (Join-Path $eclipseWorkspace 'installer\Eclipse.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$eclipseSetup = Join-Path $eclipseRelease 'eclipse-launcher-windows-x86.exe'
$eclipseHash = (Get-FileHash -LiteralPath $eclipseSetup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $eclipseRelease 'SHA256SUMS.txt'), "$eclipseHash  eclipse-launcher-windows-x86.exe`n", [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $eclipseSetup"
Write-Output "SHA256: $eclipseHash"
