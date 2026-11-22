param([string]$Version = '0.6.0')
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $root
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be MAJOR.MINOR.PATCH.' }
$release = Join-Path $root 'artifacts/release'
$stage = Join-Path $release 'eclipse-launcher-linux-x64'
$archive = Join-Path $release 'eclipse-launcher-linux-x64.tar.gz'
if (-not $stage.StartsWith((Join-Path $root 'artifacts') + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet publish src/Eclipse.Launcher.Linux/Eclipse.Launcher.Linux.csproj -c Release -r linux-x64 --self-contained true `
  -p:Version=$Version -p:PublishReadyToRun=false -p:NuGetAudit=false -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $stage 'packaging') -Force | Out-Null
Copy-Item -LiteralPath packaging/linux/eclipse-launcher -Destination (Join-Path $stage 'packaging/eclipse-launcher')
Copy-Item -LiteralPath packaging/linux/eclipse-launcher.desktop -Destination (Join-Path $stage 'packaging/eclipse-launcher.desktop')
Copy-Item -LiteralPath packaging/linux/README-LINUX.md -Destination (Join-Path $stage 'README-LINUX.md')
Copy-Item -LiteralPath THIRD-PARTY.md -Destination (Join-Path $stage 'THIRD-PARTY.md')
tar -czf $archive -C $release 'eclipse-launcher-linux-x64'
if ($LASTEXITCODE -ne 0) { throw 'Linux archive creation failed.' }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS-linux.txt') -Value "$hash  eclipse-launcher-linux-x64.tar.gz" -Encoding ascii
Write-Host "Ready: $archive"
