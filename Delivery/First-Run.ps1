<#
JMD first-use entry for a complete portable package. Place in JmdUpdater/FirstRun/.
The normal-user parent validates files, requests Windows elevation only for certificate setup,
then starts the product using the parent's original permissions. Never changes product data or ACLs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$EntryPoint,
    [Parameter(Mandatory=$true)][string]$CertificateSha256,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [switch]$ConfigureTrust,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
try {
    $taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if ([IO.Path]::GetFileName($EntryPoint) -ne $EntryPoint -or [IO.Path]::GetExtension($EntryPoint) -ne '.exe' -or
        $EntryPoint.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid product entry point.' }
    if ($CertificateSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Invalid pinned publisher identity.' }
    $taskCertificatePath = Join-Path $PSScriptRoot 'SynJMD-Internal-CodeSigning.cer'
    $taskInstallerPath = Join-Path $PSScriptRoot 'Install-InternalPublisherTrust.ps1'
    $taskProductPath = Join-Path $taskRoot $EntryPoint
    $taskUpdaterPath = Join-Path $taskRoot 'JmdUpdater\JmdUpdater.exe'
    foreach ($taskFile in @($taskCertificatePath,$taskInstallerPath,$taskProductPath,$taskUpdaterPath,$PSCommandPath)) {
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw ('Missing package file: ' + $taskFile) }
        $taskParent = [IO.Path]::GetFullPath($taskFile)
        while ($taskParent) {
            if ((Get-Item -LiteralPath $taskParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Extract the package to a regular local directory, without symbolic links or junctions.' }
            $taskParent = [IO.Path]::GetDirectoryName($taskParent)
        }
    }
    if ((Get-FileHash -LiteralPath $taskCertificatePath -Algorithm SHA256).Hash -ne $CertificateSha256) { throw 'Public certificate does not match the package publisher fingerprint.' }

    # Validate the same pinned DER policy before requesting elevation, without writing trust.
    & $taskInstallerPath -CertificatePath $taskCertificatePath -ExpectedSha256 $CertificateSha256 -ValidateOnly | Out-Null
    $taskTrusted = $true
    foreach ($taskFile in @($taskProductPath,$taskUpdaterPath)) {
        $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskFile
        # UnknownError includes an untrusted root; NotTrusted is explicit distrust and must stop before setup.
        if (-not $taskSignature.SignerCertificate -or $taskSignature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or
            $taskSignature.Status -notin @('Valid','UnknownError')) { throw ('Product signature is missing, changed or from another publisher: ' + $taskFile) }
        # Bind the certificate to be installed to the actual PE signer before any trust write.
        $taskSignerHash = [Security.Cryptography.SHA256]::Create()
        try { $taskSignerSha256 = [BitConverter]::ToString($taskSignerHash.ComputeHash($taskSignature.SignerCertificate.RawData)).Replace('-','') }
        finally { $taskSignerHash.Dispose() }
        if ($taskSignerSha256 -ne $CertificateSha256) { throw 'The public certificate and product signer do not match.' }
        if ($taskSignature.Status -ne 'Valid') { $taskTrusted = $false }
    }
    if ($CheckOnly) { Write-Output ('Package preflight passed; publisher trusted=' + $taskTrusted); exit 0 }
    if ($ConfigureTrust) {
        $taskIdentity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $taskIdentity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator authorization is required to configure machine trust.' }
        & $taskInstallerPath -CertificatePath $taskCertificatePath -ExpectedSha256 $CertificateSha256 -Scope LocalMachine
        exit 0
    }
    if (-not $taskTrusted) {
        Write-Host '首次使用需要配置公司内部发布者信任，请确认即将弹出的 Windows 管理员授权提示。'
        # Paths cannot contain quotes; use literal -File arguments, never an interpolated -Command.
        $taskArguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -EntryPoint "' + $EntryPoint +
            '" -CertificateSha256 ' + $CertificateSha256 + ' -CertificateThumbprint ' + $CertificateThumbprint + ' -ConfigureTrust'
        $taskPowerShell = Join-Path $PSHOME 'powershell.exe'
        $taskSetup = Start-Process -FilePath $taskPowerShell -ArgumentList $taskArguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($taskSetup.ExitCode -ne 0) { throw ('Certificate setup did not complete (exit ' + $taskSetup.ExitCode + '). Ask company IT to authorize this publisher.') }
    }
    foreach ($taskFile in @($taskProductPath,$taskUpdaterPath)) {
        $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskFile
        if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw ('Windows still cannot verify this publisher: ' + $taskFile) }
    }
    Write-Host '签名信任已配置完成，正在打开软件。以后直接打开主程序即可。'
    Start-Process -FilePath $taskProductPath -WorkingDirectory $taskRoot
    exit 0
} catch {
    Write-Host ('首次配置未完成：' + $_.Exception.Message) -ForegroundColor Red
    Write-Host '请保留完整解压目录。若取消了管理员确认、没有管理员凭据，或公司策略限制，请交给 IT 处理。'
    exit 1
}
