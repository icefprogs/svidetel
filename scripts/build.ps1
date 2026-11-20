param(
    [switch]$SelfContained,
    [switch]$Installer,
    [switch]$WithPresentMon,
    [string]$InstallerCompiler,
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

function Sign-Application([string]$path) {
    if (-not $SigningThumbprint) { return }
    $storeArgs = if ($certificateStore -eq 'LocalMachine') { @('/sm') } else { @() }
    & $SignTool sign @storeArgs /sha1 $SigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $path
    if ($LASTEXITCODE) { throw "Authenticode signing failed for $path" }
    & $SignTool verify /pa /all /tw $path
    if ($LASTEXITCODE) { throw "Authenticode verification failed for $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningThumbprint -or
        -not $signature.TimeStamperCertificate) { throw "Signature, publisher or timestamp verification failed for $path" }
}

function Write-Checksum([string]$path) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    Set-Content -LiteralPath "$path.sha256" -Encoding ascii -Value "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($path))"
    $hash | Format-List
}

if ($Installer -and -not $InstallerCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InstallerCompiler = $command.Source }
    else {
        $locations = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))
        $InstallerCompiler = foreach ($root in $locations | Where-Object { $_ }) {
            $path = Join-Path $root 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $path -PathType Leaf) { $path; break }
        }
    }
}
if ($Installer -and -not $InstallerCompiler) { throw 'Install Inno Setup 6 or pass -InstallerCompiler with the ISCC.exe path.' }
dotnet restore Svidetel/Svidetel.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Restore failed'}
dotnet restore tests/Svidetel.Tests.csproj --configfile NuGet.Config
if($LASTEXITCODE){throw 'Test restore failed'}
dotnet run --project tests/Svidetel.Tests.csproj -c Release --no-restore
if($LASTEXITCODE){throw 'Tests failed'}
$standalone = if ($SelfContained -or $Installer) { 'true' } else { 'false' }
$artifactRoot = Join-Path $PWD 'artifacts'
$staging = Join-Path $artifactRoot ("publish-" + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $artifactRoot 'Svidetel-windows-x64.zip'
$archive = Join-Path $artifactRoot ([IO.Path]::GetFileName($staging) + '.zip')
$payload = Join-Path $staging 'app'
$null = New-Item -ItemType Directory -Path $payload -Force
try {
    dotnet publish Svidetel/Svidetel.csproj -c Release -r win-x64 --self-contained $standalone -o $payload
    if ($LASTEXITCODE) { throw 'Publish failed' }
    Sign-Application (Join-Path $payload 'Svidetel.exe')
    if ($WithPresentMon) {
        $source = Join-Path $artifactRoot 'tools/presentmon-2.6.0'
        $null = New-Item -ItemType Directory -Path $source -Force
        $collector = Join-Path $source 'PresentMon.exe'
        $license = Join-Path $source 'LICENSE.txt'
        $collectorHash = 'b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af'
        if (-not (Test-Path -LiteralPath $collector) -or (Get-FileHash -LiteralPath $collector).Hash -ne $collectorHash) {
            Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $collector
        }
        if ((Get-FileHash -LiteralPath $collector).Hash -ne $collectorHash) { throw 'Unexpected PresentMon SHA-256.' }
        if (-not (Test-Path -LiteralPath $license)) {
            Invoke-WebRequest 'https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.6.0/LICENSE.txt' -OutFile $license
        }
        $toolFolder = New-Item -ItemType Directory -Path (Join-Path $payload 'tools')
        Copy-Item -LiteralPath $collector -Destination $toolFolder.FullName
        Copy-Item -LiteralPath $license -Destination (Join-Path $toolFolder.FullName 'PresentMon-LICENSE.txt')
    }
    Copy-Item README.md,LICENSE -Destination $payload
    $packageDocs = New-Item -ItemType Directory -Path (Join-Path $payload 'docs')
    Copy-Item -LiteralPath 'docs/screenshots' -Destination $packageDocs.FullName -Recurse
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
    if ($Installer) {
        $version = (Select-Xml -Path Svidetel/Svidetel.csproj -XPath '/Project/PropertyGroup/Version').Node.InnerText
        $installerOutput = Join-Path $staging 'installer'
        & $InstallerCompiler '/Qp' "/DAppVersion=$version" "/DPayloadDir=$payload" "/DInstallerOutput=$installerOutput" 'scripts/Svidetel.iss'
        if ($LASTEXITCODE) { throw 'Installer compilation failed' }
        $setup = Join-Path $installerOutput 'Svidetel-Setup-x64.exe'
        Sign-Application $setup
        $setupOutput = Join-Path $artifactRoot 'Svidetel-Setup-x64.exe'
        Move-Item -LiteralPath $setup -Destination $setupOutput -Force
        Write-Checksum $setupOutput
    }
    Move-Item -LiteralPath $archive -Destination $output -Force
}
finally {
    if ((Split-Path -Path $staging -Parent) -eq $artifactRoot -and (Split-Path -Path $staging -Leaf) -match '^publish-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
Write-Checksum $output
