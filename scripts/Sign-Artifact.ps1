[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [ValidateNotNullOrEmpty()][string]$FilePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SignToolPath,
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [Parameter(Mandatory, ParameterSetName = 'Preflight')]
    [ValidateNotNullOrEmpty()][string]$DlibPath,
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [Parameter(Mandatory, ParameterSetName = 'Preflight')]
    [ValidateNotNullOrEmpty()][string]$MetadataPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedPublisher,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$VerifyOnly,
    [Parameter(Mandatory, ParameterSetName = 'Preflight')][switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Resolve-InputFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    return (Get-Item -LiteralPath $Path).FullName
}

function Assert-ArtifactSignature {
    & $SignToolPath verify /pa /all /tw /v $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Signature or timestamp verification failed (exit $LASTEXITCODE): $FilePath" }
    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate) {
        throw "Authenticode validation failed: $FilePath ($($signature.Status))"
    }
    if (-not $signature.TimeStamperCertificate) { throw "A verified timestamp is required: $FilePath" }
    $publisher = $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if (-not [string]::Equals($publisher, $ExpectedPublisher, [StringComparison]::Ordinal)) {
        throw "Unexpected certificate publisher on $FilePath. Expected an exact match for the configured publisher."
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or $ExpectedPublisher -match '[\x00\r\n]') {
        throw 'ExpectedPublisher must be the exact nonempty certificate publisher name.'
    }
    $SignToolPath = Resolve-InputFile $SignToolPath
    if (-not $VerifyOnly) {
        $DlibPath = Resolve-InputFile $DlibPath
        $MetadataPath = Resolve-InputFile $MetadataPath
        $metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
        foreach ($required in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
            if ($metadata.$required -isnot [string] -or [string]::IsNullOrWhiteSpace($metadata.$required)) {
                throw "Signing metadata requires a nonempty string: $required"
            }
        }
        $endpoint = $null
        if (-not [Uri]::TryCreate($metadata.Endpoint, [UriKind]::Absolute, [ref]$endpoint) -or
            $endpoint.Scheme -ne 'https' -or $endpoint.Host -notmatch '^[a-z0-9-]+\.codesigning\.azure\.net$' -or
            $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/' -or
            -not $endpoint.IsDefaultPort) {
            throw 'Signing metadata must use the HTTPS regional Artifact Signing endpoint without credentials, paths or queries.'
        }
        foreach ($property in $metadata.PSObject.Properties.Name) {
            if ($property -notin 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName', 'CorrelationId', 'ExcludeCredentials') {
                throw "Unsupported signing metadata property: $property. Keep credentials outside this file."
            }
        }
    }
    if ($ValidateOnly) { exit 0 }
    $FilePath = Resolve-InputFile $FilePath
    if ($VerifyOnly) { Assert-ArtifactSignature; exit 0 }

    # Never replace or append to an existing publisher's signature. This also
    # checks files reused by the compiler instead of silently accepting them.
    $existing = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($existing.Status -ne 'NotSigned') { Assert-ArtifactSignature; exit 0 }
    & $SignToolPath sign /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $DlibPath /dmdf $MetadataPath $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed, including any timestamp warning (exit $LASTEXITCODE): $FilePath" }
    Assert-ArtifactSignature
    exit 0
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
