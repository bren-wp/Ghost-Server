# Ghost Server Security

## Supported development line

The active development line is `0.1.x`.

## SSH trust model

Ghost Server must not silently accept unknown SSH host keys.

On first connection the presented SHA-256 fingerprint is shown to the user and the connection is rejected until that fingerprint is explicitly approved. Future connections compare the presented fingerprint with the pinned value. A mismatch fails closed.

## Secrets

- Passwords and private-key passphrases are session-only in the current implementation.
- Secrets must never be serialized into `servers.json`.
- Secrets must not be written to logs, exception telemetry, process arguments or README examples.
- Private keys remain user-owned files and are not copied into the application data directory.

## Commands

Terminal commands are intentionally user-directed administrative actions against servers the user controls. Internal health/security probes use fixed command text; user profile values are not interpolated into those command strings.

## Reporting

Report security defects privately to the project owner before public disclosure.
