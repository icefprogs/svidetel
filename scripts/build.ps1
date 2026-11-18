param(
    [switch]$SelfContained,
    [string]$SigningThumbprint,
    [string]$TimestampUrl,
    [string]$SignTool = 'signtool.exe'
)
$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet restore Svidetel/Svidetel.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Restore failed'}
dotnet restore tests/Svidetel.Tests.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Test restore failed'}
dotnet run --project tests/Svidetel.Tests.csproj -c Release --no-restore
if($LASTEXITCODE){throw 'Tests failed'}
$standalone=if($SelfContained){'true'}else{'false'}
dotnet publish Svidetel/Svidetel.csproj -c Release -r win-x64 --self-contained $standalone -o artifacts/windows-x64
if($LASTEXITCODE){throw 'Publish failed'}
if($SigningThumbprint){
    if($SigningThumbprint -notmatch '^[a-fA-F0-9]{40}$' -or $TimestampUrl -notmatch '^https://'){
        throw 'Provide a certificate thumbprint and an HTTPS RFC 3161 timestamp URL.'
    }
    $exe=Join-Path $PWD 'artifacts/windows-x64/Svidetel.exe'
    & $SignTool sign /sha1 $SigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
    if($LASTEXITCODE){throw 'Authenticode signing failed'}
    & $SignTool verify /pa $exe
    if($LASTEXITCODE){throw 'Authenticode verification failed'}
}
Copy-Item README.md,LICENSE -Destination artifacts/windows-x64
Compress-Archive -Path artifacts/windows-x64/* -DestinationPath artifacts/Svidetel-windows-x64.zip -Force
Get-FileHash artifacts/Svidetel-windows-x64.zip -Algorithm SHA256 | Format-List
