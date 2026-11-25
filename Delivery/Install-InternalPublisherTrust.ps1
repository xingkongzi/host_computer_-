<#
JMD internal publisher trust deployment for IT-managed computers.
The operator must verify the public certificate SHA-256 through a trusted channel.
Imports only this non-CA code-signing certificate; never installs a private key or changes SmartScreen/App Control settings.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$CertificatePath,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256,
    [ValidateSet('CurrentUser','LocalMachine')][string]$Scope='CurrentUser',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$taskExpected = ($ExpectedSha256 -replace '\s','').ToUpperInvariant()
# Hash, inspect and import the same bounded DER bytes, not multiple reads of a mutable path.
$taskCertificateFile = [IO.Path]::GetFullPath($CertificatePath)
if ((Get-Item -LiteralPath $taskCertificateFile).Length -gt 1048576) { throw 'Public certificate file is too large.' }
$taskBytes = [IO.File]::ReadAllBytes($taskCertificateFile)
if ($taskBytes.Length -gt 1048576) { throw 'Public certificate file is too large.' }
$taskHash = [Security.Cryptography.SHA256]::Create()
try { $taskActual = [BitConverter]::ToString($taskHash.ComputeHash($taskBytes)).Replace('-','') }
finally { $taskHash.Dispose() }
if ($taskExpected -notmatch '^[A-F0-9]{64}$' -or
    $taskActual -ne $taskExpected) { throw 'Public certificate SHA-256 does not match the independently verified fingerprint.' }
$taskCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$taskBytes)
try {
    if ([Convert]::ToBase64String($taskBytes) -ne [Convert]::ToBase64String($taskCertificate.RawData)) { throw 'Expected one canonical DER .cer file, not PEM, PFX or a certificate bundle.' }
    if ($taskCertificate.HasPrivateKey -or $taskCertificate.Subject -ne $taskCertificate.Issuer -or
        $taskCertificate.NotBefore -gt (Get-Date) -or $taskCertificate.NotAfter -le (Get-Date)) { throw 'Expected a current self-issued public certificate without a private key.' }
    $taskUsage = @($taskCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    $taskConstraints = @($taskCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($taskUsage.Count -ne 1 -or @($taskUsage[0].EnhancedKeyUsages | Where-Object Value -eq '1.3.6.1.5.5.7.3.3').Count -ne 1 -or
        $taskUsage[0].EnhancedKeyUsages.Count -ne 1 -or $taskConstraints.Count -gt 1 -or
        ($taskConstraints.Count -eq 1 -and $taskConstraints[0].CertificateAuthority)) { throw 'Only a non-CA certificate restricted to Code Signing is accepted.' }
    if ($ValidateOnly) { Write-Output ('Validated internal publisher: ' + $taskCertificate.Thumbprint); return }
    foreach ($taskStore in @('Root','TrustedPublisher')) {
        $taskDestination = 'Cert:\' + $Scope + '\' + $taskStore
        if ($PSCmdlet.ShouldProcess($taskDestination, ('Trust internal publisher ' + $taskCertificate.Subject + ' [' + $taskCertificate.Thumbprint + ']'))) {
            $taskStoreHandle = [Security.Cryptography.X509Certificates.X509Store]::new($taskStore, $Scope)
            try {
                $taskStoreHandle.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                $taskStoreHandle.Add($taskCertificate)
            } finally { $taskStoreHandle.Close() }
            Write-Output ('Installed exact publisher certificate into ' + $taskDestination)
        }
    }
} finally { $taskCertificate.Dispose() }
