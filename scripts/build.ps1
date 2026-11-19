param(
    [switch]$SelfContained,
    [switch]$RequireSignature,
    [string]$SigningThumbprint,
    [string]$TimestampUrl,
    [string]$SignTool = 'signtool.exe'
)
$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($RequireSignature -and -not $SigningThumbprint) {
    throw 'A publisher certificate is required. Provide -SigningThumbprint and -TimestampUrl.'
}
if ($SigningThumbprint) {
    $timestamp = $null
    if ($SigningThumbprint -notmatch '^[a-fA-F0-9]{40}$' -or
        -not [Uri]::TryCreate($TimestampUrl,[UriKind]::Absolute,[ref]$timestamp) -or
        $timestamp.Scheme -ne 'https') {
        throw 'Provide a certificate thumbprint and an HTTPS RFC 3161 timestamp URL.'
    }
    $certificateStore = $null
    foreach ($store in @('CurrentUser','LocalMachine')) {
        $candidate = Get-Item "Cert:\$store\My\$SigningThumbprint" -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.HasPrivateKey) {
            $certificateStore = $store
            break
        }
    }
    if (-not $certificateStore) { throw 'Signing certificate with a private key not found in a Personal store.' }
    if ($SignTool -eq 'signtool.exe' -and -not (Get-Command $SignTool -ErrorAction SilentlyContinue)) {
        $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        if (Test-Path -LiteralPath $sdkBin) {
            $resolved = Get-ChildItem -LiteralPath $sdkBin -Directory |
                Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
                Sort-Object { [Version]$_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
                Select-Object -First 1
            if ($resolved) { $SignTool = $resolved }
        }
    }
    $null = Get-Command $SignTool -ErrorAction Stop
}
dotnet restore Svidetel/Svidetel.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Restore failed'}
dotnet restore tests/Svidetel.Tests.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Test restore failed'}
dotnet run --project tests/Svidetel.Tests.csproj -c Release --no-restore
if($LASTEXITCODE){throw 'Tests failed'}
$standalone = if ($SelfContained) { 'true' } else { 'false' }
$artifactRoot = Join-Path $PWD 'artifacts'
$staging = Join-Path $artifactRoot ("publish-" + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $artifactRoot 'Svidetel-windows-x64.zip'
$archive = Join-Path $artifactRoot ([IO.Path]::GetFileName($staging) + '.zip')
$null = New-Item -ItemType Directory -Path $staging -Force
try {
    dotnet publish Svidetel/Svidetel.csproj -c Release -r win-x64 --self-contained $standalone -o $staging
    if ($LASTEXITCODE) { throw 'Publish failed' }
    if ($SigningThumbprint) {
        $exe = Join-Path $staging 'Svidetel.exe'
        $storeArgs = if ($certificateStore -eq 'LocalMachine') { @('/sm') } else { @() }
        & $SignTool sign @storeArgs /sha1 $SigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
        if ($LASTEXITCODE) { throw 'Authenticode signing failed' }
        & $SignTool verify /pa /all /tw $exe
        if ($LASTEXITCODE) { throw 'Authenticode verification failed' }
        $signature = Get-AuthenticodeSignature -LiteralPath $exe
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningThumbprint -or
            -not $signature.TimeStamperCertificate) { throw 'Signature, publisher or timestamp verification failed' }
    }
    Copy-Item README.md,LICENSE -Destination $staging
    $packageDocs = New-Item -ItemType Directory -Path (Join-Path $staging 'docs')
    Copy-Item -LiteralPath 'docs/screenshots' -Destination $packageDocs.FullName -Recurse
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive
    Move-Item -LiteralPath $archive -Destination $output -Force
}
finally {
    if ((Split-Path -Path $staging -Parent) -eq $artifactRoot -and (Split-Path -Path $staging -Leaf) -match '^publish-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
Set-Content -LiteralPath "$output.sha256" -Encoding ascii -Value "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($output))"
$hash | Format-List
