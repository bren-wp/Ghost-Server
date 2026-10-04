# Ghost Server Safe Update

Safe Update is a guarded package-update workflow for SSH-connected Linux servers.

## Supported package managers

- APT
- DNF
- YUM
- Zypper
- pacman

## Workflow

1. Preview the server state and pending updates.
2. Confirm the Safe Update action.
3. Choose a local path for the mandatory pre-update configuration snapshot.
4. Ghost Server creates a temporary remote snapshot of allowlisted configuration.
5. The snapshot is downloaded over the same pinned-host SFTP trust path used by Files.
6. Ghost Server verifies at least 1 GiB of root-disk free space and non-interactive sudo.
7. Regular package upgrades are installed using the detected supported package manager.
8. Ghost Server does not reboot the server.
9. A post-update health report is collected.
10. The temporary remote snapshot is removed; the downloaded local snapshot remains available to the user.

## Restore scope

Restore config snapshot is a configuration rollback only. It does not promise package downgrades.

Allowed archive roots:
- `/etc/ssh`
- `/etc/nginx`
- `/etc/apache2`
- `/etc/systemd/system`
- `/etc/docker`
- `/etc/fail2ban`
- `/etc/ufw`

The restore rejects absolute paths, traversal segments, paths outside this allowlist, links and special filesystem entries. It does not restart services or reboot automatically after extraction.

## Operational note

Safe Update intentionally favors a stopped workflow over guessing. Unsupported package managers, insufficient disk space, missing sudo permission, snapshot failure or restore validation failure all block the mutation path.
