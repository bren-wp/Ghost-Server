# Ghost Server

**Ghost Server** is a Windows desktop application for managing SSH-connected Linux servers through a focused graphical interface.

The project follows the established **Ghost FTP** visual system: Electric Blue `#38ABFF`, Deep Navy `#0B1E36`, Slate Blue `#132D52`, Ice White `#EAF6FF`, a 51 px custom title bar and a 216 px navigation rail.

## Current milestone — 0.1.0 foundation

This development milestone contains real application code rather than seeded demo data:

- add and persist SSH server profiles;
- password or private-key authentication;
- session secrets stay in memory and are not written to the profile store;
- SHA-256 SSH host-key pinning with explicit first-connection approval;
- live hostname, OS, kernel, uptime, load, CPU, RAM, disk and Docker discovery;
- running systemd service list;
- command terminal against the selected server;
- read-only baseline security scan;
- dark Ghost UI implemented in WPF with no browser/WebView application shell;
- Windows CI build and self-contained x64 publish artifact.

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

Next milestones expand Docker management, logs, firewall UI, backup integration, scheduled tasks, Safe Update/rollback and signed Windows packaging.

## License

See [LICENSE](LICENSE).
