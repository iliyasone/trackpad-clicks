#!/bin/bash
# Build and stage an update on Ilias's PC without touching the running app: it lands in
# C:\TrackpadClicks\update and is applied when he clicks "Установить" (tray, settings
# window, or a trackpadclicks://update link). See Updater.cs.
#   [RESTART_CIAO=1] scripts/stage-update.sh "what's new" [driver-dir driver-version]
# driver-dir: a Magic Trackpad driver package for AMD64 (the .inf, .sys, .cat, .dll).
set -e
NOTES="$1"; DRIVER_DIR="$2"; DRIVER_VERSION="$3"
K="-o KexAlgorithms=curve25519-sha256@libssh.org,curve25519-sha256"
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' TrackpadClicks.csproj)
~/.dotnet/dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out >/dev/null

STAGE=$(mktemp -d)
cp out/TrackpadClicks.exe "$STAGE/"
python3 -c 'import json,sys,os; print(json.dumps({"version": sys.argv[1], "notes": sys.argv[2], "driver": sys.argv[3] or None, "restartCiao": os.environ.get("RESTART_CIAO") == "1"}, ensure_ascii=False))' \
  "$VERSION" "$NOTES" "${DRIVER_DIR:+$DRIVER_VERSION}" > "$STAGE/update.json"
[ -n "$DRIVER_DIR" ] && mkdir "$STAGE/driver" && cp "$DRIVER_DIR"/* "$STAGE/driver/"

ssh $K wsl "rm -rf /tmp/tc-stage"
scp -q -r $K "$STAGE" wsl:/tmp/tc-stage
rm -rf "$STAGE"
cat > /tmp/trackpad-stage.ps1 <<'PS'
$ProgressPreference = 'SilentlyContinue'
$dir = 'C:\TrackpadClicks\update'
if (Test-Path $dir) { Get-ChildItem $dir -Exclude 'applied-*' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$env:USERPROFILE\tc-stage\*" $dir -Recurse -Force
Remove-Item "$env:USERPROFILE\tc-stage" -Recurse -Force
# The staged exe opens trackpadclicks:// links until it is installed; the installed one
# registers itself at start-up after that.
$k = 'HKCU:\Software\Classes\trackpadclicks'
New-Item -Force "$k\shell\open\command" | Out-Null
Set-ItemProperty $k '(default)' 'URL:TrackpadClicks'
Set-ItemProperty $k 'URL Protocol' ''
Set-ItemProperty "$k\shell\open\command" '(default)' "`"$dir\TrackpadClicks.exe`" --url `"%1`""
Get-ChildItem $dir -Recurse | ForEach-Object { $_.FullName.Substring($dir.Length + 1) }
Get-Content "$dir\update.json"
PS
B64=$(iconv -f UTF-8 -t UTF-16LE /tmp/trackpad-stage.ps1 | base64 -w0)
ssh $K wsl "scp -q -r /tmp/tc-stage windows:C:/Users/iliya/ && rm -rf /tmp/tc-stage && ssh windows 'powershell -NoProfile -EncodedCommand $B64'" 2>&1 | grep -v CLIXML | sed 's/<[^>]*>//g'
