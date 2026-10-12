#!/usr/bin/env bash
# Builds the Linux release: a self-contained linux-x64 build packed as
#   artifacts/linux/sessionmanager-<version>-linux-x64.tar.gz
# Needs the .NET 10 SDK. Works on Linux, and on Windows from Git Bash (cross-compiles).
#
#   packaging/linux/build.sh            # release tarball
#   packaging/linux/build.sh --arch     # also builds the Arch package from it (needs makepkg)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SessionManager.Linux/SessionManager.Linux.csproj | head -1)"
name="sessionmanager-$version-linux-x64"
out="$root/artifacts/linux"
stage="$out/$name"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
echo "Session Manager $version: publishing (self-contained, linux-x64)..."
rm -rf "$stage"
dotnet publish src/SessionManager.Linux/SessionManager.Linux.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishReadyToRun=true -p:DebugType=none -o "$stage/app" -nologo -v q

cp packaging/linux/sessionmanager.desktop packaging/linux/sessionmanager-mime.xml packaging/linux/install.sh "$stage/"
cp -r packaging/linux/icons "$stage/icons"
cp LICENSE "$stage/" 2>/dev/null || true
chmod 755 "$stage/install.sh" "$stage/app/sessionmanager" "$stage/app/createdump" 2>/dev/null || true

echo "Packing..."
rm -f "$out/$name.tar.gz"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    # Windows file systems have no executable bit: write the archive with Linux permissions set explicitly.
    python - "$out" "$name" <<'PY'
import os, sys, tarfile
out, name = sys.argv[1], sys.argv[2]
exe = {f"{name}/app/sessionmanager", f"{name}/app/createdump", f"{name}/install.sh"}
def fix(ti):
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    ti.mode = 0o755 if ti.isdir() or ti.name in exe else 0o644
    return ti
with tarfile.open(os.path.join(out, name + ".tar.gz"), "w:gz") as t:
    t.add(os.path.join(out, name), arcname=name, filter=fix)
PY
    ;;
  *) tar -C "$out" --owner=0 --group=0 -czf "$out/$name.tar.gz" "$name" ;;
esac
rm -rf "$stage"
( cd "$out" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256" )
echo "Done: $out/$name.tar.gz ($(du -h "$out/$name.tar.gz" | cut -f1))"
cat "$out/$name.tar.gz.sha256"

if [[ "${1:-}" == "--arch" ]]; then
  cp "$out/$name.tar.gz" packaging/arch-bin/
  ( cd packaging/arch-bin && makepkg -f --cleanbuild )
  echo "Arch package: $(ls packaging/arch-bin/*.pkg.tar.zst)"
fi
