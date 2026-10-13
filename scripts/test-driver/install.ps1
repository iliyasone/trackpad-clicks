# Installs a test-signed Magic Trackpad driver package (from a fork's CI build) that sits
# next to this script. Run elevated; TrackpadClicks' updater does that when the package
# is staged with scripts/stage-update.sh. Exit 3010: done, a restart is needed.
#
# A test-signed driver loads only in Windows test mode, which needs Secure Boot off.
# Changing the boot settings makes BitLocker ask for the recovery key on the next start,
# so BitLocker is suspended for that one restart first.
# Undo: bcdedit /set testsigning off, then install the signed release again.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Start-Transcript -Path 'C:\TrackpadClicks\driver-install.log' -Force | Out-Null
try {
    if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State').UEFISecureBootEnabled -eq 1) {
        Write-Output 'Secure Boot is on: turn it off in the firmware settings first.'
        exit 20
    }
    $bitlocker = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction SilentlyContinue
    if ($bitlocker -and $bitlocker.ProtectionStatus -eq 'On') { Suspend-BitLocker -MountPoint 'C:' -RebootCount 1 | Out-Null; Write-Output 'BitLocker suspended for one restart' }
    bcdedit /set testsigning on
    if ($LASTEXITCODE) { exit 21 }
    $cer = Get-ChildItem $here -Filter *.cer | Select-Object -First 1
    foreach ($store in 'Root', 'TrustedPublisher') { Import-Certificate -FilePath $cer.FullName -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null }
    pnputil /add-driver (Get-ChildItem $here -Filter *.inf | Select-Object -First 1).FullName /install
    if ($LASTEXITCODE -notin 0, 3010) { exit $LASTEXITCODE }
    exit 3010
}
finally { Stop-Transcript | Out-Null }
