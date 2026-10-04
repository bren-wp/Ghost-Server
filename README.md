# Ghost Server

**Ghost Server** is a Windows desktop application for managing SSH-connected Linux servers through a focused graphical interface.

The project follows the established **Ghost FTP** visual system: Electric Blue `#38ABFF`, Deep Navy `#0B1E36`, Slate Blue `#132D52`, Ice White `#EAF6FF`, a 51 px custom title bar and a 216 px navigation rail.

## Current release — 0.6.0

This development milestone contains real application code rather than seeded demo data:

- add, edit and delete SSH server profiles;
- password or private-key authentication;
- session secrets stay in memory and are not written to the profile store;
- SHA-256 SSH host-key pinning with explicit first-connection approval;
- live hostname, OS, kernel, uptime, load, CPU, RAM, disk and Docker discovery;
- running systemd service list;
- Services workspace with start, stop and restart actions;
- Docker container discovery and start/stop/restart actions;
- recent server, service and Docker log viewer with client-side filtering;
- secure SFTP file browser with upload and download;
- Network workspace for interfaces, routes, listening sockets and supported firewall state;
- validated UFW/firewalld allow-port workflow with explicit confirmation;
- Safe Update preview and guarded package upgrades for APT, DNF, YUM, Zypper and pacman;
- mandatory downloaded configuration snapshot before Safe Update changes;
- post-update health verification with no automatic reboot;
- validated configuration restore for allowlisted Ghost Server snapshot paths;
- configuration snapshot workflow with SFTP download and temporary-archive cleanup;
- configurable 15/30/60/120-second dashboard auto-refresh;
- terminal command history and keyboard shortcuts;
- explicit session lock that clears the in-memory secret;
- resettable SSH host-key trust for deliberate re-pinning;
- command terminal against the selected server;
- read-only baseline security scan;
- dark Ghost UI implemented in WPF with Fluent iconography, branded executable/installer icon and no browser/WebView application shell;
- Windows CI build with verified `GhostServer-Portable.exe` and `GhostServer-Setup.exe` artifacts;
- portable launch smoke test plus Setup silent install/launch/uninstall smoke test;
- atomic profile/settings persistence with `.bak` recovery;
- Settings & About workspace with default backup folder and profile import/export;
- bounded local crash diagnostics for unexpected failures;
- .NET analyzers enabled with warnings treated as build errors;
- automatic GitHub Release publication only after a successful main CI run.

## Technology

- C# / .NET 10
- WPF
- SSH.NET 2026.0.0
- Windows 10/11

## Build

```powershell
dotnet restore GhostServer.sln
dotnet build GhostServer.sln -c Release
dotnet run --project src/GhostServer/GhostServer.csproj
```

Self-contained Windows publish:

```powershell
dotnet publish src/GhostServer/GhostServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Security principles

Ghost Server is designed to fail closed around SSH identity:

- a server host key is never silently trusted;
- a changed fingerprint blocks connection;
- passwords/passphrases are not persisted by the current profile store;
- routine UI errors must not expose secrets;
- security scan operations in this milestone are read-only.

See [SECURITY.md](SECURITY.md).

## Product direction

Next milestones focus on scheduled operations, database tooling, notification channels and signed Windows packaging.

## License

See [LICENSE](LICENSE).
