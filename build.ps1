param([switch]$Test, [switch]$Publish)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$installed=Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet=if($installed){$installed.Source}else{Join-Path (Split-Path -Parent $root) 'build\usage-lite-tools\dotnet\dotnet.exe'}
if(-not(Test-Path -LiteralPath $dotnet)){throw 'Please install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$project=Join-Path $root 'UsageLite.csproj'
& $dotnet build $project -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Build failed'}
if($Test){
 $artifacts=Join-Path $root 'artifacts'
 New-Item -ItemType Directory -Path $artifacts -Force|Out-Null
 & $dotnet (Join-Path $root 'bin\Release\net10.0-windows\UsageLite.dll') --self-test --fixtures (Join-Path $root 'tests\fixtures') --output (Join-Path $artifacts 'test-results.json')
 if($LASTEXITCODE -ne 0){throw 'Tests failed; see artifacts\test-results.json'}
 Get-Content -LiteralPath (Join-Path $artifacts 'test-results.json') -Raw
}
if($Publish){
 & $dotnet publish $project -c Release -r win-x64 --self-contained true -o (Join-Path $root 'release') --nologo
 if($LASTEXITCODE -ne 0){throw 'Publish failed'}
 Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'README.md'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $root 'release')
 New-Item -ItemType Directory -Path (Join-Path $root 'release\licenses') -Force | Out-Null
 Copy-Item -Path (Join-Path $root 'licenses\*') -Destination (Join-Path $root 'release\licenses') -Force
}
