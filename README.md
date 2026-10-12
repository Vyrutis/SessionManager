# Session Manager

A Windows desktop app for SSH and Remote Desktop (RDP) connections. Your servers are kept in a folder tree, sessions open as tabs in one window, and passwords are stored in Windows Credential Manager.

Built with .NET 10 and WPF. SSH uses [SSH.NET](https://github.com/sshnet/SSH.NET) with an [xterm.js](https://xtermjs.org) terminal in WebView2. RDP uses the Remote Desktop ActiveX control that comes with Windows.

## Features

### Sessions
- **SSH and RDP in tabs.** Open many sessions side by side. You can reconnect, duplicate or close tabs, and use Ctrl+Tab and Ctrl+Shift+W.
- **Folder tree.** SSH and RDP sessions sit in separate groups with any number of folders. You can drag and drop, filter (Ctrl+F), and expand or collapse all.
- **Quick connect.** Type `user@host[:port]` or `rdp://host` in the toolbar.
- **Connect as…** Open a saved session with different credentials once.
- **Desktop shortcuts.** Create a shortcut that opens one session directly. A second launch hands the session to the window that is already running.
- **Ping host.** Checks ICMP and whether the session's TCP port answers.
- **Default presets.** Save one session's settings as the defaults for new sessions.

### SSH
- A full terminal with 256 colors, search, clickable links and resizing. There are several color schemes (Campbell, One Dark, Dracula, …), or "Auto" to follow the app theme.
- **SFTP file panel** next to the terminal, for browsing, uploading and downloading files.
- **Port forwarding:** local (`L 8080:db:5432`), remote (`R 9000:localhost:3000`) and dynamic SOCKS (`D 1080`).
- **Jump hosts.** Reach a server through another SSH session.
- **Private key** authentication.
- **Host key checking.** Remembers fingerprints and warns when a server's key changes.
- **Multi-exec.** Send one command to several SSH tabs at once.

### Remote Desktop
- Embedded RDP with fit-to-tab or a fixed resolution, color depth and smart sizing.
- Clipboard, drive and printer redirection.
- Domain support. Domains you use are offered in a drop-down before the user name, and can be starred to keep them.

### Credentials
- Passwords are saved only in **Windows Credential Manager**, never in the app's own files.
- **Credential profiles.** One user name and password shared by many sessions; change it once and every session uses the new one.

### Import and export
- **Session lists** (`.smlist`) hold the whole tree or one folder, with credential profiles. Saved passwords can be included, encrypted with a passphrase you choose. On import you can merge (duplicates are skipped) or replace.
- **Single sessions** as `.smsession` files, and `.rdp` files in both directions.
- **Import from other tools.** Hosts, ports, users, domains and folders are imported. Passwords that other programs keep encrypted are not read; you can type them in the review screen.

  | Source | What is read |
  |---|---|
  | PuTTY | Saved sessions (from the registry) |
  | OpenSSH | `~/.ssh/config` (aliases, HostName, Port, User, IdentityFile) |
  | Windows Remote Desktop (mstsc) | Computers you have connected to, with the remembered user name |
  | `.rdp` files on this PC | Searches Desktop, Documents, Downloads and OneDrive, up to 5 levels deep. Online-only OneDrive files are skipped. |
  | mRemoteNG | `confCons.xml`, including its folder structure |
  | MobaXterm | `.mxtsessions` / `MobaXterm.ini` bookmarks |
  | CSV | KeePass, Bitwarden and spreadsheet exports. Columns are found by name (host / IP / URL, user, password, port, protocol, group). Web logins are skipped. |

- **Scan a screenshot or PDF.** Paste a screenshot (Ctrl+Shift+V), or pick an image or PDF. Windows' built-in text recognition reads it on your PC; nothing is uploaded. Server addresses are found along with the user name and password next to each one. This works for:
  - tables with a header (`Name | IP | User | Password`), even when the OCR misses a column title;
  - values written beside an IP with no header (`10.0.0.5  admin  Secret1`, `admin:Secret1`, `admin / Secret1`);
  - labelled blocks (`Host: …` / `Username: …` / `Password: …`);
  - `user@host:port`, `ssh://` and `rdp://` addresses;
  - RDP rows with `DOMAIN\user`.

  The protocol is guessed from words like *RDP*, *Windows* or *Linux* and from ports 22 and 3389.
- **Review before adding.** Every import opens an editable list:
  - tick or untick entries and fix any misread values;
  - show or hide passwords;
  - optionally put everything in a new folder;
  - skip connections you already have;
  - save identical logins as one shared credential profile.

### Appearance
- **Silver (light) and Midnight (dark)** themes, switched from the title bar.
- **Appearance menu** (palette icon next to the theme switch):
  - Accent and main color, from presets or a full color picker (HSV square, hue bar, hex input).
  - A custom background picture, with how much shows through and an optional blur.
  - Interface size, 80–150 %. Menus, dialogs and pop-ups scale with it.
  - Animations on or off.
- The toolbar switches to icons only when the window is narrow.
- A short fade-out when the window closes.

## Requirements

- Windows 10 1809 or later, or Windows 11, x64
- Microsoft Edge WebView2 Runtime (included in Windows 11 and current Windows 10)
- For screenshot and PDF scanning: a Windows display language with text recognition support (English and most others)

## Install

Download `SessionManager-<version>.msi` from [Releases](../../releases) and run it. The installer is self-contained (it includes the .NET runtime), installs per-machine, and adds Start-menu and optional desktop shortcuts.

The MSI is **not code-signed**, so Windows SmartScreen may show "Windows protected your PC". Choose *More info → Run anyway*.

App data (the session tree, settings and known host keys) is stored in `%APPDATA%\SessionManager`. Set the `SESSIONMANAGER_DATA` environment variable to use another folder; that copy then runs as a separate instance.

## Build from source

Requires the .NET 10 SDK.

```powershell
dotnet build SessionManager.sln
dotnet run --project src/SessionManager
```

Run the tests (a console runner; it includes real OCR of a generated image and PDF):

```powershell
dotnet run --project tests/SessionManager.Tests
```

Build the MSI (uses WiX v5 via NuGet; the output goes to `artifacts\installer\`):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

## Linux

Not supported at the moment. The app is built on Windows-only parts: WPF, the Remote Desktop ActiveX control, WebView2, Windows Credential Manager and Windows OCR. A Linux version would mean porting the interface to a cross-platform toolkit such as Avalonia, with FreeRDP, libsecret and Tesseract in place of the Windows parts.

## Project layout

```
src/SessionManager/          the app
  MainWindow.*               main window, tree, tabs, menus, import wiring
  SshTerminalView, SshConnector, SftpPanel, KnownHosts   SSH
  RdpView                    Remote Desktop
  ConnectionImport           PuTTY / ssh config / mstsc / .rdp search / mRemoteNG / MobaXterm / CSV
  ScreenshotScan             OCR (images, PDF pages) and connection extraction
  ImportReviewWindow         review screen for every import
  Theme, ThemeMenu, ColorPicker, CloseAnimation          appearance
  CredentialStore            Windows Credential Manager
  wwwroot/                   xterm.js terminal page
tests/SessionManager.Tests/  console test runner
installer/                   WiX MSI project and build script
```
