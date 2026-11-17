param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.test-data\dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build .\src\Eclipse.Launcher\Eclipse.Launcher.csproj -c Release --nologo -m:1
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($Test) {
    dotnet run --project .\tests\Eclipse.Tests\Eclipse.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
dotnet publish .\src\Eclipse.Launcher\Eclipse.Launcher.csproj -c Release --no-restore --self-contained false -o .\artifacts\Eclipse --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Write-Host "Ready: $PSScriptRoot\artifacts\Eclipse\Eclipse.exe"
