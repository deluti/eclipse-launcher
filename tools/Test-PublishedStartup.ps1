param(
    [string]$PublishDirectory,
    [string]$ExpectedVersion = '0.6.0',
    [ValidateRange(20,120)][int]$Seconds = 30
)
$ErrorActionPreference='Stop'
$eclipseWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PublishDirectory) { $PublishDirectory=Join-Path $eclipseWorkspace 'artifacts\release\win-x86' }
$eclipseApplication=[IO.Path]::GetFullPath((Join-Path $PublishDirectory 'Eclipse.exe'))
if (-not $eclipseApplication.StartsWith($eclipseWorkspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Startup tests must use a workspace copy' }
$eclipseTestHost=Join-Path $eclipseWorkspace 'tests\Eclipse.Tests\bin\Release\net10.0-windows\Eclipse.Tests.dll'
$eclipseAssembly=Join-Path $PublishDirectory 'Eclipse.dll'
dotnet $eclipseTestHost --verify-published-assembly $eclipseAssembly
if ($LASTEXITCODE -ne 0) { throw 'Unsafe publication mode' }
$eclipseCases=Join-Path $eclipseWorkspace ('.test-data\published-startup-'+[guid]::NewGuid().ToString('N'))
dotnet $eclipseTestHost --startup-fixtures $eclipseCases
if ($LASTEXITCODE -ne 0) { throw 'Startup fixtures failed' }
$eclipseResults=@()
foreach ($eclipseCase in @('local','cached-microsoft')) {
    $eclipseData=Join-Path $eclipseCases $eclipseCase
    $eclipseStart=New-Object Diagnostics.ProcessStartInfo
    $eclipseStart.FileName=$eclipseApplication
    $eclipseStart.WorkingDirectory=[IO.Path]::GetDirectoryName($eclipseApplication)
    $eclipseStart.UseShellExecute=$false
    $eclipseStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $eclipseStart.Arguments='--data-dir "'+$eclipseData+'"'
    # No environment workaround is allowed to mask a defective release.
    $eclipseStart.EnvironmentVariables.Remove('DOTNET_ReadyToRun')
    $eclipseStart.EnvironmentVariables.Remove('COMPlus_ReadyToRun')
    $eclipseProcess=[Diagnostics.Process]::Start($eclipseStart)
    try {
        if ($eclipseProcess.WaitForExit($Seconds*1000)) { throw "Published $eclipseCase startup exited early: $($eclipseProcess.ExitCode)" }
        $eclipseDiagnostic=Get-Content -LiteralPath (Join-Path $eclipseData 'diagnostics\startup.json') -Raw | ConvertFrom-Json
        if ($eclipseDiagnostic.launcherVersion -ne $ExpectedVersion) { throw 'Wrong published version' }
        $eclipseProcess.CloseMainWindow() | Out-Null
        if (-not $eclipseProcess.WaitForExit(5000)) { throw 'Published launcher did not close normally' }
        if ($eclipseProcess.ExitCode -ne 0) { throw "Published launcher failed on close: $($eclipseProcess.ExitCode)" }
        $eclipseResults += [pscustomobject]@{case=$eclipseCase;version=$eclipseDiagnostic.launcherVersion;aliveSeconds=$Seconds;exitCode=$eclipseProcess.ExitCode;firstPaintMs=$eclipseDiagnostic.firstPaintMs}
    } finally {
        if (-not $eclipseProcess.HasExited) { $eclipseProcess.Kill(); $eclipseProcess.WaitForExit() }
        $eclipseProcess.Dispose()
    }
}
$eclipseResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $eclipseWorkspace '.test-data\published-startup-result.json') -Encoding UTF8
$eclipseResults | Format-Table -AutoSize
