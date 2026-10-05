# Ghost Server Security

## Supported development line

The active development line is `0.10.x`

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


## Terminal Quick Commands

Quick Commands are convenience presets for read-only inspection. Selecting a preset only copies the command into the Terminal command editor. Ghost Server does not execute the preset until the user explicitly presses Run.

The preset library is intentionally limited to inspection-oriented commands such as system summary, process listing, listening sockets, disk usage, Docker container listing and recent journal errors. It does not include destructive shell commands.
