param(
    [string]$SetupPath,
    [string]$LegacyDirectory,
    [string]$TestHostPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion = '0.5.0',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$LegacyVersion = '0.4.0'
)
$ErrorActionPreference = 'Stop'
$eclipseWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SetupPath) { $SetupPath = Join-Path $eclipseWorkspace 'artifacts\release\eclipse-launcher-windows-x86.exe' }
if (-not $LegacyDirectory) { $LegacyDirectory = Join-Path $eclipseWorkspace '.test-data\legacy-0.4-runtime' }
if (-not $TestHostPath) { $TestHostPath = Join-Path $eclipseWorkspace 'tests\Eclipse.Tests\bin\Release\net10.0-windows\Eclipse.Tests.exe' }
$eclipseTestRoot = Join-Path $eclipseWorkspace ('.test-data\upgrade-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $eclipseTestRoot -Force | Out-Null
function RegistrySnapshot {
    $eclipseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
    try {
        $eclipseInstall = $eclipseKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{3BEB6AE3-2E24-4790-890F-F469AF69C4F3}_is1')
        if (-not $eclipseInstall) { return '' }
        try {
            $eclipseValues = [ordered]@{}
            foreach ($eclipseName in ($eclipseInstall.GetValueNames() | Sort-Object)) { $eclipseValues[$eclipseName] = $eclipseInstall.GetValue($eclipseName) }
            return ($eclipseValues | ConvertTo-Json -Compress)
        } finally { $eclipseInstall.Dispose() }
    } finally { $eclipseKey.Dispose() }
}
$eclipseOriginalRegistration = RegistrySnapshot
$eclipseRealShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Eclipse Launcher\Eclipse Launcher.lnk'
$eclipseOriginalShortcutHash = if (Test-Path -LiteralPath $eclipseRealShortcut) { (Get-FileHash -LiteralPath $eclipseRealShortcut).Hash } else { '' }
function Run-Eclipse([string]$Path, [string[]]$Arguments) {
    $eclipseProcess = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $eclipseProcess.WaitForExit(30000)) { throw "Timed out: $Path" }
    if ($eclipseProcess.ExitCode -ne 0) { throw "Process exit $($eclipseProcess.ExitCode): $Path" }
}
function Seed-Data([string]$Directory) {
    $eclipseContent = @{
        'playtime.json' = '{"TotalSeconds":7200}';
        'profiles.json' = '[{"Id":"fabric-loader-1.2.3-26.3","GameVersion":"26.3","Loader":"fabric","LoaderVersion":"1.2.3"}]';
        'settings.json' = '{"Version":"26.3","OfflineMode":true,"Nickname":"Upgrade_Test"}';
        'versions.json' = '{"latest":{"release":"26.3"},"versions":[{"id":"26.3","type":"release"}]}';
        'game\saves\probe\level.dat' = 'world-preservation-probe';
        'appearance\library.json' = '{"SelectedId":null,"Skins":[]}';
        'appearance\local-skins\probe.png' = 'skin-preservation-probe';
        'instances\instance-upgrade\mods\probe.jar' = 'instance-preservation-probe';
        'game\mods\probe.jar' = 'mod-preservation-probe'
    }
    $eclipseHashes = @{}
    foreach ($eclipseRelative in $eclipseContent.Keys) {
        $eclipseFile = Join-Path $Directory $eclipseRelative
        New-Item -ItemType Directory -Path (Split-Path $eclipseFile -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($eclipseFile, $eclipseContent[$eclipseRelative])
        $eclipseHashes[$eclipseFile] = (Get-FileHash -LiteralPath $eclipseFile).Hash
    }
    return $eclipseHashes
}
function Check-Hashes($Hashes) {
    foreach ($eclipseFile in $Hashes.Keys) {
        if ((Get-FileHash -LiteralPath $eclipseFile).Hash -ne $Hashes[$eclipseFile]) { throw "Changed user file: $eclipseFile" }
    }
}
function Seed-Legacy([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Get-ChildItem -LiteralPath $LegacyDirectory | Copy-Item -Destination $Directory -Recurse -Force
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory 'Eclipse.dll')).FileVersion -ne ($LegacyVersion + '.0')) { throw "Expected actual legacy $LegacyVersion files" }
}
function Uninstall-Test([string]$Directory) {
    if (-not ([IO.Path]::GetFullPath($Directory)).StartsWith(($eclipseTestRoot + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall must stay in isolated test directory' }
    Run-Eclipse (Join-Path $Directory 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    for ($eclipseTry=0; $eclipseTry -lt 50 -and (Test-Path -LiteralPath (Join-Path $Directory 'Eclipse.exe')); $eclipseTry++) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath (Join-Path $Directory 'Eclipse.exe')) { throw 'Application was not uninstalled' }
}
$eclipseManual = Join-Path $eclipseTestRoot 'Legacy Eclipse'
Seed-Legacy $eclipseManual
$eclipseManualHashes = Seed-Data (Join-Path $eclipseManual 'data')
$eclipseConfig = Join-Path $eclipseManual 'launcher-config.json'
[IO.File]::AppendAllText($eclipseConfig, "`n")
$eclipseManualHashes[$eclipseConfig] = (Get-FileHash -LiteralPath $eclipseConfig).Hash
$eclipseLog = Join-Path $eclipseTestRoot 'manual-upgrade.log'
Run-Eclipse $SetupPath @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=""','/ECLIPSETESTINSTALL=1',('/DIR="'+$eclipseManual+'"'),('/LOG="'+$eclipseLog+'"'))
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $eclipseManual 'Eclipse.dll')).FileVersion -ne ($ExpectedVersion + '.0')) { throw 'Manual upgrade failed' }
Check-Hashes $eclipseManualHashes
if ([IO.File]::ReadAllText($eclipseLog) -notmatch ([regex]::Escape('Existing Eclipse detected: ' + $LegacyVersion + '.0'))) { throw 'Legacy-folder detection did not run' }
Run-Eclipse (Join-Path $eclipseManual 'Eclipse.exe') @('--smoke-test')
$eclipseSmoke = Get-Content -LiteralPath (Join-Path $eclipseManual 'data\startup.json') -Raw | ConvertFrom-Json
Uninstall-Test $eclipseManual
Check-Hashes $eclipseManualHashes

$eclipseAutomatic = Join-Path $eclipseTestRoot 'Automatic Eclipse'
Seed-Legacy $eclipseAutomatic
$eclipseAutoHashes = Seed-Data (Join-Path $eclipseAutomatic 'data')
$eclipseCustomData = Join-Path $eclipseTestRoot 'Custom Game Data'
$eclipseCustomHashes = Seed-Data $eclipseCustomData
& $TestHostPath --start-launcher-update $SetupPath $eclipseAutomatic $eclipseCustomData
if ($LASTEXITCODE -ne 0) { throw 'Update handoff failed' }
$eclipseExpectedExe = Join-Path $eclipseAutomatic 'Eclipse.exe'
$eclipseRestarted = $null
for ($eclipseTry=0; $eclipseTry -lt 100; $eclipseTry++) {
    $eclipseRestarted = Get-CimInstance Win32_Process -Filter "Name='Eclipse.exe'" | Where-Object { $_.ExecutablePath -eq $eclipseExpectedExe } | Select-Object -First 1
    if ($eclipseRestarted) { break }
    Start-Sleep -Milliseconds 200
}
if (-not $eclipseRestarted) { throw 'Updated launcher did not restart' }
try {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $eclipseAutomatic 'Eclipse.dll')).FileVersion -ne ($ExpectedVersion + '.0')) { throw 'Automatic upgrade failed' }
    if ($eclipseRestarted.CommandLine -notlike ('*'+$eclipseCustomData+'*')) { throw 'Restart lost custom --data-dir' }
    Check-Hashes $eclipseAutoHashes
    Check-Hashes $eclipseCustomHashes
} finally {
    # Only the process in this test's verified workspace directory may be stopped.
    if ($eclipseRestarted.ExecutablePath -ne $eclipseExpectedExe -or -not $eclipseExpectedExe.StartsWith(($eclipseTestRoot + '\'))) { throw 'Unexpected restart process' }
    Stop-Process -Id $eclipseRestarted.ProcessId -Force -ErrorAction SilentlyContinue
}
Uninstall-Test $eclipseAutomatic
Check-Hashes $eclipseAutoHashes
Check-Hashes $eclipseCustomHashes
if ((RegistrySnapshot) -ne $eclipseOriginalRegistration) { throw 'Real installation registration changed' }
if ($eclipseOriginalShortcutHash) {
    if ((Get-FileHash -LiteralPath $eclipseRealShortcut).Hash -ne $eclipseOriginalShortcutHash) { throw 'Real installation shortcut changed' }
} elseif (Test-Path -LiteralPath $eclipseRealShortcut) { throw 'Test unexpectedly created a Start menu shortcut' }
$eclipseResult = [ordered]@{ legacyUpgrade=$true; existingFolderDetected=$true; profilesWorldsModsHoursPreserved=$true; installedSmokeMs=$eclipseSmoke.firstPaintMs; automaticUpdateRestart=$true; customDataPreserved=$true; uninstallPreservesData=$true; realInstallationUntouched=$true; directory=$eclipseTestRoot }
$eclipseResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $eclipseWorkspace '.test-data\upgrade-result.json') -Encoding UTF8
$eclipseResult | ConvertTo-Json
