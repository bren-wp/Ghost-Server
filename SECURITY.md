# Ghost Server Security

## Supported development line

The active development line is `0.4.x`

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
