# Session Manager

A desktop app for SSH, Remote Desktop (RDP) and VNC connections. Your servers are kept in a folder tree, sessions open as tabs in one window, and passwords are kept in the system's password store, never in the app's files.

There are two apps built from one code base:

| | Windows | Linux (Arch) |
|---|---|---|
| Interface | WPF | [Avalonia](https://avaloniaui.net) |
| SSH terminal | [xterm.js](https://xtermjs.org) in WebView2 | built-in terminal emulator |
| Remote Desktop | Windows Remote Desktop control, inside the tab | [FreeRDP](https://www.freerdp.com) 3, in its own window or inside the tab |
| VNC | built-in viewer, inside the tab | built-in viewer, inside the tab |
| Saved passwords | Windows Credential Manager | desktop keyring (GNOME Keyring, KWallet, KeePassXC) via Secret Service |
| Screenshot / PDF scan | Windows text recognition | [Tesseract](https://github.com/tesseract-ocr/tesseract) + poppler |
| Installer | `.msi` | Arch package (`PKGBUILD`) or tarball with `install.sh` |
| Version in this branch | 1.2.0 | 1.3.0 |
| Latest release download | [v1.1.0](../../releases/tag/v1.1.0) (no VNC yet) | [v1.2.0](../../releases/tag/v1.2.0) (no VNC yet) |

VNC is new in Windows 1.2.0 / Linux 1.3.0. Until those are released, build them from source: [Windows](#build), [Arch](#install-on-arch-from-source-includes-vnc).

Both use [SSH.NET](https://github.com/sshnet/SSH.NET) and .NET 10, and read and write the same session files, so a list exported on one runs on the other.

- [Features](#features)
- [Windows](#windows): install, build
- [Linux / Arch](#linux--arch): install, what to install alongside it, desktop notes
- [Building and developing on Linux](#building-and-developing-on-linux): step by step from a fresh clone
- [Project layout](#project-layout)

## Features

### Sessions
- **SSH, RDP and VNC in tabs.** Open many sessions side by side. You can reconnect, duplicate or close tabs, and use Ctrl+Tab and Ctrl+Shift+W.
- **Folder tree.** SSH, RDP and VNC sessions sit in separate groups with any number of folders. You can drag and drop, filter (Ctrl+F), and expand or collapse all.
- **Quick connect.** Type `user@host[:port]`, `rdp://host` or `vnc://host` (also `vnc://host:1` for display 1, `vnc://host::5901` for a port) in the toolbar.
- **Connect as…** Open a saved session with different credentials once.
- **Shortcuts.** On Windows, a desktop shortcut; on Linux, an entry in the application menu (launcher search finds it). Either opens one session directly, and a second launch hands the session to the window that is already open.
- **Ping host.** Checks ICMP and whether the session's TCP port answers.
- **Default presets.** Save one session's settings as the defaults for new sessions.

### SSH
- A full terminal with 256 and 24-bit colors, scrollback, search (Ctrl+Shift+F), clickable links (Ctrl+click on Linux), copy/paste (Ctrl+Shift+C / Ctrl+Shift+V) and resizing. Full-screen programs (vim, htop, mc, tmux) work, including mouse support. Several color schemes (Campbell, One Dark, Dracula, Solarized, Gruvbox, Nord), or "Auto" to follow the app theme.
- **SFTP file panel** next to the terminal: browse, upload (also by dropping files on it), download, rename, delete, and edit a remote file in your editor with upload on save.
- **Port forwarding:** local (`L 8080:db:5432`), remote (`R 9000:localhost:3000`) and dynamic SOCKS (`D 1080`).
- **Jump hosts.** Reach a server (SSH, RDP or VNC) through another SSH session.
- **Private key** authentication (`~/.ssh/id_ed25519`, `id_ecdsa`, `id_rsa` are tried automatically).
- **Host key checking.** Remembers fingerprints and warns when a server's key changes.
- **Multi-exec.** Send what you type to several SSH tabs at once.

### Remote Desktop
- Fit to the window or a fixed resolution, color depth and scaling.
- Clipboard, drive (home folder on Linux) and printer redirection.
- Domain support. Domains you use are offered before the user name.

### VNC
- A VNC viewer built into the app (no extra program needed), showing the remote screen inside the tab. Works with UltraVNC, TightVNC, TigerVNC, x11vnc, RealVNC (with its VNC password option) and other RFB 3.3–3.8 servers.
- **Login:** the classic VNC password, or none. The password is saved like any other (Credential Manager / keyring) and can come from a credential profile, so many machines can share one VNC password. Servers that only offer other login methods (UltraVNC MS-Logon, encryption plugins, VeNCrypt/TLS, macOS Screen Sharing) get a clear message saying so; set them to use a VNC password.
- **Screen updates** in the ZRLE, Hextile, Zlib, CopyRect and Raw encodings; the remote desktop may change size during the session.
- **Toolbar:** Ctrl+Alt+Del, Windows key, *Scale to fit* (off: actual size with scroll bars), *View only*, Refresh, Disconnect.
- **Keyboard, mouse and wheel** go to the remote computer; Ctrl/Alt shortcuts work, typed characters (including accents and other alphabets) are sent as characters.
- **Clipboard:** text copied on the remote computer arrives on yours; text you copy is sent when you click back into the VNC tab. Can be turned off per session.
- **Encryption:** plain VNC is not encrypted. Choose an *SSH gateway* (jump host) in the session to tunnel the connection through SSH.

### Credentials
- Passwords are saved only in the system's password store (see the table above).
- **Credential profiles.** One user name and password shared by many sessions; change it once and every session uses the new one.

### Import and export
- **Session lists** (`.smlist`) hold the whole tree or one folder, with credential profiles. Saved passwords can be included, encrypted with a passphrase you choose. On import you can merge (duplicates are skipped) or replace.
- **Single sessions** as `.smsession` files, `.rdp` files in both directions, and `.vnc` files (UltraVNC, TightVNC, RealVNC) for import.
- **Import from other tools.** Hosts, ports, users, domains and folders are imported. Passwords that other programs keep encrypted are not read; you can type them in the review screen.

  | Source | Windows | Linux | What is read |
  |---|---|---|---|
  | Remmina | | ✓ | `~/.local/share/remmina/*.remmina` (also Flatpak): RDP, SSH and VNC profiles, groups, display options |
  | PuTTY | ✓ (registry) | ✓ (`~/.putty/sessions`) | Saved SSH sessions |
  | OpenSSH | ✓ | ✓ | `~/.ssh/config` (aliases, HostName, Port, User, IdentityFile) |
  | Windows Remote Desktop (mstsc) | ✓ | | Computers you have connected to, with the remembered user name |
  | `.rdp` files | ✓ | ✓ | Searches Desktop, Documents, Downloads (and OneDrive on Windows), up to 5 levels deep |
  | mRemoteNG | ✓ | ✓ | `confCons.xml` (SSH, RDP, VNC), including its folder structure |
  | MobaXterm | ✓ | ✓ | `.mxtsessions` / `MobaXterm.ini` bookmarks (SSH, RDP, VNC) |
  | VNC viewers | ✓ | ✓ | `.vnc` connection files from UltraVNC, TightVNC and RealVNC: host, port or display number, view-only and scaling (the stored password is not read) |
  | CSV | ✓ | ✓ | KeePass, Bitwarden and spreadsheet exports. Columns are found by name (host / IP / URL, user, password, port, protocol, group). Web logins are skipped. |

- **Scan a screenshot or PDF.** Paste a screenshot (Ctrl+Shift+V), or pick an image or PDF. Text recognition runs on your computer; nothing is uploaded. Server addresses are found along with the user name and password next to each one:
  - tables with a header (`Name | IP | User | Password`), even when a column title is misread;
  - values written beside an IP with no header (`10.0.0.5  admin  Secret1`, `admin:Secret1`, `admin / Secret1`);
  - labelled blocks (`Host: …` / `Username: …` / `Password: …`);
  - `user@host:port`, `ssh://`, `rdp://` and `vnc://` addresses;
  - RDP rows with `DOMAIN\user`.

  The protocol is guessed from words like *RDP*, *VNC*, *UltraVNC*, *Windows* or *Linux* (also in a heading right above the address) and from ports 22, 3389 and 5900–5999. VNC entries keep only the password.
- **Review before adding.** Every import opens an editable list: tick or untick entries and fix misread values, show or hide passwords, optionally put everything in a new folder, skip connections you already have, and save identical logins as one shared credential profile.

### Appearance
- Light and dark themes, switched from the toolbar.
- Accent color (presets or any hex color) and interface size, 80–150 %.
- Windows only: main color, background picture, close animation.

## Windows

### Requirements
- Windows 10 1809 or later, or Windows 11, x64
- Microsoft Edge WebView2 Runtime (included in Windows 11 and current Windows 10)
- For screenshot and PDF scanning: a Windows display language with text recognition support (English and most others)

### Install
Download `SessionManager-<version>.msi` from [Releases](../../releases) and run it (the released 1.1.0 has no VNC yet; build 1.2.0 with the commands under [Build](#build)). The installer is self-contained (it includes the .NET runtime), installs per-machine, and adds Start-menu and optional desktop shortcuts.

The MSI is **not code-signed**, so Windows SmartScreen may show "Windows protected your PC". Choose *More info → Run anyway*.

App data (the session tree, settings and known host keys) is stored in `%APPDATA%\SessionManager`. Set the `SESSIONMANAGER_DATA` environment variable to use another folder; that copy then runs as a separate instance.

### Build
Requires the .NET 10 SDK.

```powershell
dotnet build src/SessionManager
dotnet run --project src/SessionManager
dotnet run --project tests/SessionManager.Tests        # tests, including real OCR of a generated image and PDF, and the VNC tab against a test server
powershell -ExecutionPolicy Bypass -File installer\build.ps1   # MSI (WiX v5 via NuGet) → artifacts\installer\
```

## Linux / Arch

### Install on Arch (prebuilt, no .NET SDK needed)

The repository is private, so download the release with the GitHub CLI:

```bash
sudo pacman -S --needed git github-cli base-devel
gh auth login
gh repo clone Vyrutis/SessionManager
cd SessionManager/packaging/arch-bin
gh release download v1.2.0 -p 'sessionmanager-*-linux-x64.tar.gz'
makepkg -si
```

`makepkg -si` installs the dependencies from the table below, then the package `sessionmanager-bin`: the app in `/usr/lib/sessionmanager`, the `sessionmanager` command, an application-menu entry, icons and the file types `.smlist` / `.smsession`. Remove it with `sudo pacman -R sessionmanager-bin`.

### Install on Arch from source (includes VNC)

The released v1.2.0 package has no VNC yet. To get the current version (1.3.0, with VNC), build the package from the repository. This needs the .NET 10 SDK, which `makepkg` installs for the build:

```bash
sudo pacman -S --needed git github-cli base-devel
gh auth login
gh repo clone Vyrutis/SessionManager      # or, if you cloned before: cd SessionManager && git pull
cd SessionManager/packaging/arch
makepkg -si
```

This installs the package `sessionmanager` (it replaces `sessionmanager-bin` if that is installed). More detail in [Building and developing on Linux](#building-and-developing-on-linux).

### Other distributions (or without pacman)

```bash
tar xzf sessionmanager-1.2.0-linux-x64.tar.gz
cd sessionmanager-1.2.0-linux-x64
./install.sh               # for your user, into ~/.local      (sudo ./install.sh --system → /usr/local)
./install.sh --uninstall   # removes it again; your sessions stay
```

`install.sh` lists any of the optional programs below that are missing.

### What it uses on the system

| Package (Arch) | Needed for | Installed by the PKGBUILD |
|---|---|---|
| `freerdp` (3.x) | Remote Desktop sessions (`xfreerdp3`, or `sdl-freerdp3`) | yes |
| `libsecret` | saving passwords (`secret-tool`) | yes |
| `gnome-keyring` **or** KWallet **or** KeePassXC (Secret Service on) | the keyring itself | optional: install one if your desktop has none (KDE Plasma has KWallet; GNOME has GNOME Keyring) |
| `tesseract`, `tesseract-data-eng` | scanning screenshots and PDFs (add e.g. `tesseract-data-deu` for other languages; your desktop language is used when installed) | optional |
| `poppler` | scanning PDFs (`pdftoppm`) | optional |
| `wl-clipboard` (Wayland) / `xclip` (X11) | pasting screenshots when the clipboard isn't read directly | optional |
| `xdg-utils` | opening links and files edited over SFTP | optional |
| `ttf-dejavu` | terminal font (any monospace font works; JetBrains Mono, Hack or Noto Sans Mono are used if installed) | yes |

```bash
sudo pacman -S --needed gnome-keyring tesseract tesseract-data-eng poppler wl-clipboard xclip xdg-utils
```

### Where things are
- Sessions, settings, known host keys: `~/.config/SessionManager/` (`sessions.json` is the same format as on Windows; `linux.json` holds the Linux-only settings). `SESSIONMANAGER_DATA=/some/dir sessionmanager` runs a separate copy.
- Passwords: in the keyring, labelled "Session Manager", with the attributes `application=sessionmanager id=<session id>`. See them in Seahorse ("Passwords and Keys") or KWalletManager; look one up with `secret-tool lookup application sessionmanager id <id>`.
- FreeRDP's trusted server certificates: `~/.config/freerdp/server/`.
- Crash log, if something goes wrong: `~/.config/SessionManager/crash.log`.
- Application-menu entries for single sessions: `~/.local/share/applications/sessionmanager-<id>.desktop`.

### Desktop notes
- **Wayland and X11.** The app runs on X11, and on Wayland (KDE Plasma, GNOME, Hyprland, Sway…) through XWayland, which those desktops include. If text is too small or large, change *Settings → Interface size*.
- **Remote Desktop** opens in FreeRDP's own window by default: it can go full screen (Ctrl+Alt+Enter), resizes the remote desktop with the window, and grabs the keyboard. *Settings → Show the remote desktop inside the tab* draws it inside the tab instead (X11/XWayland, experimental). The password is handed to FreeRDP on its standard input, so it never shows in the process list. A server's certificate is trusted the first time and checked after that.
- **VNC** is drawn inside the tab by the app itself; nothing extra needs to be installed. The VNC password is the only login it uses, so the session dialog has no user name for VNC. To encrypt the connection, pick an *SSH gateway* in the session.
- **Keyring.** If saving a password fails, no Secret Service keyring is running or it is locked. On minimal window managers start `gnome-keyring-daemon` with your session, or enable "Secret Service" in KeePassXC. Without a keyring the app still works and asks for passwords when connecting.
- **Keyboard in the terminal.** Ctrl+Shift+C / Ctrl+Shift+V copy and paste, Shift+Insert pastes, Shift+PageUp/PageDown scroll, Ctrl+Shift+F finds, Ctrl+Shift+Plus/Minus change the font size, middle-click pastes. Every other key goes to the server.

## Building and developing on Linux

Everything below runs from a fresh clone on Arch. Other distributions work the same once the .NET 10 SDK is installed.

### 1. Tools

```bash
sudo pacman -S --needed git github-cli base-devel dotnet-sdk
dotnet --list-sdks          # must show 10.x
```

If `dotnet --list-sdks` shows an older version, install `dotnet-sdk-bin` from the AUR, or Microsoft's install script: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0` (then add `~/.dotnet` to `PATH`).

### 2. Get the code

```bash
gh auth login
gh repo clone Vyrutis/SessionManager
cd SessionManager
```

### 3. Run it from source

```bash
dotnet run --project src/SessionManager.Linux
```

Use a throw-away data folder while developing, so your real sessions aren't touched:

```bash
SESSIONMANAGER_DATA=/tmp/sm-dev dotnet run --project src/SessionManager.Linux
```

### 4. Test

```bash
dotnet run --project tests/SessionManager.Linux.Tests                 # terminal emulator, importers, FreeRDP arguments, OCR parsing, VNC client against a test server, window rendering
dotnet run --project tests/SessionManager.Linux.Tests -- /tmp/sm-shots # the same, and saves pictures of the windows there
```

The Windows test project (`tests/SessionManager.Tests`) only builds on Windows. The shared code it covers lives in `src/SessionManager.Core`, so run both suites when changing it.

### 5. Build the package

Arch package straight from your checkout (uses the `PKGBUILD` in `packaging/arch`):

```bash
cd packaging/arch
makepkg -si          # build, then install with pacman
cd ../..
```

Release tarball for any distribution (plus the prebuilt Arch package with `--arch`):

```bash
packaging/linux/build.sh            # → artifacts/linux/sessionmanager-<version>-linux-x64.tar.gz (+ .sha256)
packaging/linux/build.sh --arch     # also makes packaging/arch-bin/sessionmanager-bin-<version>-1-x86_64.pkg.tar.zst
```

### 6. Make a new version

1. Raise `<Version>` in `src/SessionManager.Linux/SessionManager.Linux.csproj` and `pkgver` in both `packaging/arch/PKGBUILD` and `packaging/arch-bin/PKGBUILD` (reset `pkgrel` to 1).
2. Run the tests and `packaging/linux/build.sh`.
3. Commit, tag and publish:
   ```bash
   git commit -am "Session Manager 1.2.1"
   git tag v1.2.1 && git push && git push --tags
   gh release create v1.2.1 artifacts/linux/sessionmanager-1.2.1-linux-x64.tar.gz{,.sha256} --title "Session Manager 1.2.1" --notes "…"
   ```

### Where to change what

| To change | Look in |
|---|---|
| Session tree, file formats, imports, OCR parsing, SSH connection, keyring | `src/SessionManager.Core` (shared by both apps) |
| Linux window, toolbar, menus, tree, tabs | `src/SessionManager.Linux/MainWindow*.cs` |
| Terminal (escape sequences / drawing / keys) | `src/SessionManager.Linux/Terminal/VtTerminal.cs`, `TerminalView.cs` |
| Remote Desktop (FreeRDP command line, exit messages) | `src/SessionManager.Linux/FreeRdp.cs`, `RdpTab.cs` |
| VNC protocol (login, screen encodings, keys) | `src/SessionManager.Core/Vnc/` (shared) |
| VNC tab (drawing, mouse, keyboard, clipboard, toolbar) | `src/SessionManager.Linux/VncTab.cs`, Windows: `src/SessionManager/VncView.cs` |
| Colors, theme | `src/SessionManager.Linux/UiTheme.cs`, `Terminal/TerminalScheme.cs` |
| Package contents, dependencies | `packaging/arch/PKGBUILD`, `packaging/arch-bin/PKGBUILD`, `packaging/linux/` |

## Project layout

```
src/SessionManager.Core/     shared by both apps (no UI)
  Models, SessionStore       session tree and sessions.json
  SessionFile, SessionList   .smsession / .rdp / .smlist import and export
  ConnectionImport           Remmina / PuTTY / ssh config / mstsc / .rdp search / mRemoteNG / MobaXterm / .vnc / CSV
  ScreenshotScan             finding connections in recognized text (rows, columns, labels)
  SshConnector, KnownHosts   SSH login, jump hosts, port forwards, host keys
  CredentialStore            Windows Credential Manager, or SecretService (Linux keyring)
  Vnc/                       VNC client: VncClient (RFB 3.3–3.8, ZRLE/Hextile/Zlib/CopyRect/Raw), VncAuth (DES), VncKeys
src/SessionManager/          the Windows app (WPF)
  MainWindow.*, SessionDialog, ImportReviewWindow, Theme*, ColorPicker, CloseAnimation
  SshTerminalView, SftpPanel, RdpView, VncView, WindowsOcr, wwwroot/ (xterm.js)
src/SessionManager.Linux/    the Linux app (Avalonia)
  MainWindow*.cs             window, tree, tabs, import / export / scan
  SshTab, SftpPanel          SSH tab and file panel
  Terminal/                  terminal emulator (VtTerminal) and its view
  RdpTab, FreeRdp            Remote Desktop through FreeRDP
  VncTab                     VNC tab (screen, input, clipboard, toolbar)
  TesseractOcr, ClipboardImage   scanning on Linux
  SessionDialog, CredentialsWindow, ImportReviewWindow, SettingsWindow, Dialogs, UiTheme
tests/SessionManager.Tests/        Windows test runner
tests/SessionManager.Linux.Tests/  Linux test runner (also runs on Windows)
installer/                   WiX MSI project and build script (Windows)
packaging/linux/             desktop entry, icons, file types, install.sh, build.sh (tarball)
packaging/arch/              PKGBUILD that builds from this checkout
packaging/arch-bin/          PKGBUILD for the prebuilt release tarball
```
