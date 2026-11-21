$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appOut = Join-Path $env:TEMP "guardex-app-publish"
$setupOut = Join-Path $env:TEMP "guardex-setup-publish"
$artifacts = Join-Path $root "artifacts"
if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
dotnet publish (Join-Path $root "Guardex.csproj") -c Release -r win-x64 --self-contained false -o $appOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $root "Guardex.Browser\Guardex.Browser.csproj") -c Release -r win-x64 --self-contained false -o $appOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$zip = Join-Path $root "Setup\payload.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $appOut "*") -DestinationPath $zip
if (Test-Path $setupOut) { Remove-Item -Recurse -Force $setupOut }
dotnet publish (Join-Path $root "Setup\Setup.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $setupOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Copy-Item (Join-Path $setupOut "GUARDEX-Setup.exe") (Join-Path $artifacts "GUARDEX-Setup.exe") -Force
Write-Output (Join-Path $artifacts "GUARDEX-Setup.exe")
