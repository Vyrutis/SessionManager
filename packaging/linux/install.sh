#!/usr/bin/env bash
# Installs Session Manager from the release tarball (any distribution; on Arch the PKGBUILD is nicer).
#
#   ./install.sh                     install for the current user (~/.local)
#   sudo ./install.sh --system       install for everyone (/usr/local)
#   ./install.sh --uninstall         remove the user install   (add --system for the system one)
#
# Your sessions (~/.config/SessionManager) and saved passwords (keyring) are never touched.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mode=user
action=install
for arg in "$@"; do
  case "$arg" in
    --system) mode=system ;;
    --uninstall) action=uninstall ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [[ $mode == system ]]; then
  prefix=/usr/local
  [[ $EUID -eq 0 ]] || { echo "A system install needs root: sudo $0 $*" >&2; exit 1; }
else
  prefix="$HOME/.local"
fi
lib="$prefix/lib/sessionmanager"
bin="$prefix/bin/sessionmanager"
share="${XDG_DATA_HOME:-$HOME/.local/share}"
[[ $mode == system ]] && share="$prefix/share"

refresh() {
  command -v update-desktop-database >/dev/null && update-desktop-database -q "$share/applications" 2>/dev/null || true
  command -v update-mime-database >/dev/null && update-mime-database "$share/mime" >/dev/null 2>&1 || true
  command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$share/icons/hicolor" 2>/dev/null || true
}

if [[ $action == uninstall ]]; then
  rm -rf "$lib"
  rm -f "$bin" "$share/applications/sessionmanager.desktop" "$share/mime/packages/sessionmanager.xml"
  for size in 16 32 48 64 128 256; do rm -f "$share/icons/hicolor/${size}x${size}/apps/sessionmanager.png"; done
  refresh
  echo "Session Manager removed. Your sessions in ~/.config/SessionManager were kept."
  exit 0
fi

[[ -x "$here/app/sessionmanager" || -f "$here/app/sessionmanager" ]] || { echo "Run this from the unpacked release folder (app/ is missing)." >&2; exit 1; }

echo "Installing to $lib"
rm -rf "$lib"
mkdir -p "$lib" "$(dirname "$bin")"
cp -r "$here/app/." "$lib/"
# Archives made on Windows lose the executable bit; set permissions explicitly.
find "$lib" -type f -exec chmod 644 {} +
chmod 755 "$lib/sessionmanager" "$lib/createdump" 2>/dev/null || true
ln -sf "$lib/sessionmanager" "$bin"

install -Dm644 "$here/sessionmanager.desktop" "$share/applications/sessionmanager.desktop"
# ~/.local/bin is not always on PATH for the desktop session: use the full path.
sed -i "s|^Exec=sessionmanager|Exec=$bin|" "$share/applications/sessionmanager.desktop"
install -Dm644 "$here/sessionmanager-mime.xml" "$share/mime/packages/sessionmanager.xml"
for size in 16 32 48 64 128 256; do
  install -Dm644 "$here/icons/sessionmanager-$size.png" "$share/icons/hicolor/${size}x${size}/apps/sessionmanager.png"
done
refresh

echo "Installed. Start it from the application menu or run: $bin"
missing=()
command -v xfreerdp3 >/dev/null || command -v sdl-freerdp3 >/dev/null || command -v xfreerdp >/dev/null || missing+=("freerdp (Remote Desktop)")
command -v secret-tool >/dev/null || missing+=("libsecret + a keyring such as gnome-keyring (saving passwords)")
command -v tesseract >/dev/null || missing+=("tesseract + tesseract-data-eng (scanning screenshots)")
command -v pdftoppm >/dev/null || missing+=("poppler (scanning PDFs)")
if ((${#missing[@]})); then
  echo
  echo "Optional programs not found:"
  printf '  - %s\n' "${missing[@]}"
  echo "On Arch: sudo pacman -S freerdp libsecret gnome-keyring tesseract tesseract-data-eng poppler"
fi
