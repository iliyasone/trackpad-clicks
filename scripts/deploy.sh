#!/bin/bash
# Build, copy to C:\TrackpadClicks on Ilias's PC, (re)register the logon task, restart.
set -e
K="-o KexAlgorithms=curve25519-sha256@libssh.org,curve25519-sha256"
cd "$(dirname "$0")/.."
~/.dotnet/dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out >/dev/null
scp -q $K out/TrackpadClicks.exe wsl:/tmp/
cat > /tmp/trackpad-install.ps1 <<'PS'
$ProgressPreference = 'SilentlyContinue'
Get-Process TrackpadClicks -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
New-Item -ItemType Directory -Force C:\TrackpadClicks | Out-Null
# A copy wedged in the kernel keeps its exe locked; a locked exe can still be renamed.
Get-ChildItem C:\TrackpadClicks\TrackpadClicks.old-*.exe -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
if (Test-Path C:\TrackpadClicks\TrackpadClicks.exe) { Rename-Item C:\TrackpadClicks\TrackpadClicks.exe "TrackpadClicks.old-$(Get-Date -Format yyyyMMddHHmmss).exe" }
Move-Item "$env:USERPROFILE\TrackpadClicks.exe" C:\TrackpadClicks\TrackpadClicks.exe -Force
$a = New-ScheduledTaskAction -Execute 'C:\TrackpadClicks\TrackpadClicks.exe'
$t = New-ScheduledTaskTrigger -AtLogOn -User 'iliya'
$p = New-ScheduledTaskPrincipal -UserId 'iliya' -LogonType Interactive -RunLevel Limited
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'TrackpadClicks' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
Remove-Item C:\TrackpadClicks\debug -ErrorAction SilentlyContinue; Remove-Item "$env:LOCALAPPDATA\TrackpadClicks\log.txt" -ErrorAction SilentlyContinue
Stop-ScheduledTask -TaskName 'TrackpadClicks'
Start-ScheduledTask -TaskName 'TrackpadClicks'
Start-Sleep 3
Get-Process TrackpadClicks -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited } | ForEach-Object { "running pid $($_.Id) session $($_.SessionId)" }
Get-Content "$env:LOCALAPPDATA\TrackpadClicks\log.txt" -Tail 10 -ErrorAction SilentlyContinue
PS
B64=$(iconv -f UTF-8 -t UTF-16LE /tmp/trackpad-install.ps1 | base64 -w0)
ssh $K wsl "scp -q /tmp/TrackpadClicks.exe windows:C:/Users/iliya/ && rm /tmp/TrackpadClicks.exe && ssh windows 'powershell -NoProfile -EncodedCommand $B64'" 2>&1 | grep -v CLIXML | sed 's/<[^>]*>//g'
