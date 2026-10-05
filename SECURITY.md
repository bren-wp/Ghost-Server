# Ghost Server Security

## Supported development line

The active development line is `0.19.x`

## SSH trust model

Ghost Server must not silently accept unknown SSH host keys.

On first connection the presented SHA-256 fingerprint is shown to the user and the connection is rejected until that fingerprint is explicitly approved. Future connections compare the presented fingerprint with the pinned value. A mismatch fails closed.

## Secrets

- Passwords and private-key passphrases are session-only in the current implementation.
- Secrets must never be serialized into `servers.json`.
- Secrets must not be written to logs, exception telemetry, process arguments or README examples.
- Private keys remain user-owned files and are not copied into the application data directory.

## SFTP

SFTP uses the same pinned SSH host identity as command sessions. Unknown or changed host keys fail closed. Upload and download operations use the SSH.NET SFTP API rather than shell command interpolation.

## Profile trust reset

Resetting a pinned host key is an explicit local action. The next connection must present and re-approve a fingerprint before SSH or SFTP operations can proceed.

## Commands

Terminal commands are intentionally user-directed administrative actions against servers the user controls. Internal health/security probes use fixed command text; user profile values are not interpolated into those command strings.

## Reporting

Report security defects privately to the project owner before public disclosure.


## Network and firewall operations

Network discovery is read-only. The only firewall mutation exposed in 0.4.x is an explicit allow-port action for validated port numbers and TCP/UDP protocols. Ghost Server does not automatically create deny rules, remove rules, disable a firewall or alter the SSH port. Administrative firewall changes use non-interactive `sudo -n`.

## Configuration snapshots

Configuration snapshots use a fixed allowlist of common server configuration directories and are created as temporary archives. The archive is downloaded through the same pinned-host SFTP channel used by the Files workspace. Ghost Server then attempts to remove the temporary remote archive whether the local download succeeds or fails.

## Package updates

The Updates workspace is discovery-only in 0.4.x. It does not install, upgrade or remove packages.


## Local application data recovery

Profile and settings JSON files are written atomically through a temporary file and retain a `.bak` copy of the previous valid state. If the primary JSON becomes malformed, Ghost Server attempts to load the backup rather than silently accepting corrupted data.

Profile export/import never includes passwords or private-key passphrases because those values are session-only and are not members of the persisted profile model.

## Runtime diagnostics

Unexpected process-level failures may write a bounded local diagnostic log under the Ghost Server app-data `Logs` directory. Ghost Server does not intentionally include session secrets in these diagnostics. Fatal WPF dispatcher exceptions are logged but are not swallowed so the application does not continue in an unknown state.


## Safe Update

Safe Update is an explicit remote mutation and never runs automatically. Before any package change, Ghost Server requires a configuration snapshot to be created remotely and downloaded to the local Windows PC over the verified SFTP channel.

Additional guardrails:
- the remote root filesystem must have at least 1 GiB free;
- the connected account must have non-interactive `sudo -n` access;
- only APT, DNF, YUM, Zypper and pacman are supported;
- APT preserves existing configuration files with `--force-confold`;
- Ghost Server does not issue an automatic reboot;
- the post-update health report is read-only and reports failed systemd units, disk state, reboot-required state and remaining updates.

## Configuration restore

Configuration restore is not presented as a universal package rollback. Ghost Server uploads the selected local snapshot over verified SFTP to a generated `/tmp/ghost-server-restore-<guid>.tar.gz` path. Before extraction, the remote archive must pass all of these checks:
- valid gzip tar archive;
- no absolute paths;
- no `..` path traversal segments;
- every entry must remain inside the allowlist used by Ghost Server snapshots;
- only regular files and directories are accepted; symbolic links, hard links and special filesystem entries are rejected.

The restore does not automatically restart services or reboot the server. The temporary uploaded archive is deleted after the restore attempt.


## Scheduled operations

Ghost Server scheduled tasks are isolated from unrelated systemd timers and user cron entries:
- managed timer and service units must use the `ghost-server-<name>` namespace;
- task names are normalized to lowercase and limited to letters, numbers and hyphens with a maximum of 40 characters;
- only Hourly, Daily and Weekly schedules are generated by the GUI;
- task scripts are transferred as base64 content and written to `/usr/local/lib/ghost-server/tasks` with root ownership and mode 700;
- creating and deleting tasks requires explicit confirmation and non-interactive `sudo -n`;
- deletion removes only the matching Ghost Server timer, service and task script;
- the current user's crontab is displayed read-only and is never modified by this workspace.

Scheduled commands are intentionally administrator-supplied commands and run as root. Ghost Server does not silently generate or schedule commands on behalf of the user.


## System workspace

Process, filesystem, block-device, load and logged-in-user discovery are read-only. Process termination is the only mutation exposed by the System workspace in 0.8.x.

Ghost Server:
- accepts only the numeric PID selected from the current process inventory;
- rejects PID 1 and lower;
- requires explicit user confirmation;
- sends SIGTERM only;
- does not automatically escalate to SIGKILL;
- uses non-interactive sudo only when the connected account cannot signal the process directly.


## Fleet probes

Fleet inventory itself is local metadata. Bulk Fleet probes are restricted to profiles that already have a pinned SSH host key and use private-key authentication. Ghost Server supplies no password or passphrase during a bulk probe.

The selected-server Fleet probe accepts a session secret only for that selected row. The secret is read from the WPF PasswordBox, used for that probe, and cleared immediately afterward. Fleet never persists it.

Fleet health collection is read-only and uses the same verified SSH host identity as Dashboard health discovery.


## Remote operation lifecycle safety

Starting with 0.16.x, selected-server remote operations capture the selected profile, session secret, operation generation and cancellation token before the first remote await.

This prevents asynchronous work from silently crossing a server-selection boundary:
- changing the selected server cancels the previous selected-server operation generation;
- Lock session, SSH trust reset, active-profile edit/delete and application shutdown also invalidate selected-server remote work;
- remote results are written back to the UI only if the generation and selected profile still match;
- administrative confirmation text is built from the captured target profile rather than a later mutable selection;
- maintenance cleanup for temporary Safe Update, restore and backup files intentionally uses the captured original profile even when the UI has moved elsewhere;
- cancellation sources used by in-flight operations are not disposed until application shutdown.

These protections do not weaken the existing pinned-host-key model, single-mutation gate or session-only secret policy.

## Persistent interactive terminal

The Terminal workspace in 0.15.x opens one persistent SSH `ShellStream` for the selected profile instead of creating a new command connection for every submitted line.

Security boundaries:
- the shell is created only through the same verified SSH client path used by other Ghost Server SSH operations;
- a profile without an approved pinned host key cannot open the shell;
- a changed host key fails closed through the existing fixed-time fingerprint comparison;
- passwords and private-key passphrases remain session-only and are not written to disk;
- only one interactive shell is active for the UI session;
- server selection changes, profile edits/deletes, SSH trust reset, Lock session and window close tear down the shell;
- terminal output is displayed locally and bounded in memory; Ghost Server does not persist shell output or upload it to telemetry;
- commands entered in Terminal are explicitly user-directed remote commands and are not treated as safe/read-only operations.

The v0.15 terminal is line-oriented. It does not claim to be a full-screen terminal emulator and does not reinterpret terminal applications as structured Ghost Server actions.

## Terminal Quick Commands

Quick Commands are convenience presets for read-only inspection. Selecting a preset only copies the command into the Terminal command editor. Ghost Server does not execute the preset until the user explicitly presses Run.

The preset library is intentionally limited to inspection-oriented commands such as system summary, process listing, listening sockets, disk usage, Docker container listing and recent journal errors. It does not include destructive shell commands.


## Database discovery

The Database workspace is read-only in 0.11.x.

Ghost Server detects PostgreSQL, MySQL/MariaDB and SQLite clients over the already verified SSH connection. It may list database names only when the remote host already permits non-interactive local authentication for the connected account or through an existing passwordless sudo/socket policy.

Ghost Server does not:
- store database passwords;
- read remote database credential files;
- prompt for or cache database-specific credentials;
- inspect table contents or row data;
- expose CREATE, ALTER, DROP, INSERT, UPDATE, DELETE or other SQL mutation actions.

SQLite discovery reports the installed client version only. Ghost Server does not crawl the remote filesystem for SQLite database files.


## Fleet health history

Fleet health history is local application state stored under the Ghost Server app-data directory using the same atomic JSON write and backup-recovery mechanism used by other local settings.

Each record may contain:
- server profile ID;
- probe timestamp;
- health status;
- CPU, memory and disk utilization percentages;
- system load text;
- a short local attention or failure summary.

Fleet history never stores passwords, passphrases, private-key contents, database credentials or remote command output. Ghost Server does not upload Fleet health history or send it to any third-party service.

History retention is bounded to the latest 100 records per server and 2,000 records total. Clearing history affects only local Ghost Server state and does not modify the remote server.


## Alert Center

The Alert Center is derived exclusively from local Fleet health history.

Acknowledging an alert:
- writes only a local acknowledgement timestamp;
- does not modify the remote server;
- does not change the server's current Fleet health state;
- does not suppress future health records.

CSV export may contain server profile name, timestamps, local status, acknowledgement time, CPU/RAM/disk percentages, load text and the local attention/failure summary. It never includes passwords, passphrases, private-key material, database credentials or remote command output.

Ghost Server does not upload Alert Center data or send it to a third-party service.


## Fleet Trends

Fleet Trends is calculated entirely from the bounded local Fleet health history already stored by Ghost Server. It does not contact remote servers when refreshing or exporting the comparison.

Trend CSV export contains server profile name, sample counts, local Healthy/Attention/failure counts, aggregate CPU/RAM/disk percentages and latest local status. It does not contain passwords, passphrases, private-key material, database credentials or remote command output.

## Release artifact provenance

Starting with 0.14.x, GitHub Release publication consumes the `GhostServer-windows-release` artifact from the exact successful main Windows CI run that triggered the release workflow.

The release workflow does not rebuild Portable or Setup binaries. Published binaries are therefore the same bytes that passed the main CI Portable launch and Setup install/launch/uninstall smoke tests. SHA-256 and build-provenance manifests are generated from those validated binaries.
