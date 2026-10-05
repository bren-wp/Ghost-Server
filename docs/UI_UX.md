# Ghost Server UI / UX contract

Ghost Server inherits the established Ghost FTP desktop visual language while using Windows WPF controls and no browser/WebView application shell.

## Canonical frame

- reference window: **1290 × 852**
- custom title bar: **51 px**
- main navigation rail: **216 px**
- content header: **84 px**
- status bar: **38 px**

The layout must remain usable when resized. Dense content scrolls locally; the complete application must never be globally scaled like an image.

## Core colors

- Electric Blue: `#38ABFF`
- Deep Navy: `#0B1E36`
- Slate Blue: `#132D52`
- Ice White: `#EAF6FF`

Use blue emphasis selectively. Dark navy surfaces remain dominant and thin blue borders define hierarchy.

## Interaction requirements

- every visible action must be backed by a real action;
- no production demo servers, fake online states, fake metrics or fake command output;
- SSH host-key approval is an in-window security surface, not an automatic trust path;
- passwords/passphrases are visibly session-only;
- terminal and security output use a monospace font;
- errors remain actionable and do not leak credentials;
- keyboard focus and resizing must remain usable.

## Ghost FTP parity targets

Ghost Server should feel like a member of the same product family: title-bar geometry, sidebar density, card radius, restrained glow, typography and spacing should remain aligned with Ghost FTP. Server-management content is allowed to diverge where the workflow requires it.


## v0.3 interaction polish

- navigation uses Segoe Fluent Icons with a visible active state;
- the executable and installer use the Ghost Server branded icon;
- destructive profile deletion always requires an in-app confirmation;
- server cards expose Edit, Reset trust and Delete without hiding critical actions in context menus;
- session secrets have an explicit Lock session control;
- keyboard shortcuts: Ctrl+N adds a server, Ctrl+L clears the session secret, F5 refreshes the active workspace;
- Files uses SFTP and separates path navigation from transfer actions;
- log search filters the currently loaded output locally without issuing extra remote commands;
- terminal Up/Down recalls the latest local command history.


## v0.4 operations UX

- the navigation rail scrolls independently so every workspace remains reachable at supported minimum window sizes;
- Network separates read-only discovery from the explicit firewall mutation action;
- firewall changes always require a confirmation dialog and validated port/protocol input;
- Updates is intentionally read-only and communicates that boundary next to the primary action;
- Backup presents one focused create-and-download workflow and reports remote creation, local download and cleanup phases;
- Dashboard auto-refresh is opt-in and automatically stops when the session is locked or a refresh fails.


## v0.5 completion polish

- the title-bar version badge is populated from assembly metadata instead of duplicated hard-coded text;
- Settings & About centralizes monitoring interval, backup folder, app-data access and profile portability;
- server-list dots are neutral profile markers and no longer imply a live connection before one exists;
- dashboard, Services and Docker action rows wrap instead of clipping at narrower supported widths;
- disruptive Service and Docker stop/restart actions require confirmation;
- only one remote mutation action may execute at a time, preventing accidental overlapping administrative commands;
- crash diagnostics are local, bounded and surfaced through the app-data location.


## v0.6 Safe Update UX

- the former Updates workspace is labeled **Safe Update** to make the mutation path explicit;
- Preview updates remains read-only and shows host, kernel, root free space, existing reboot-required state, failed units and pending packages;
- Run Safe Update requires an explicit warning confirmation and a chosen local snapshot destination before any package mutation;
- progress is shown as four explicit stages: create snapshot, download snapshot, install updates, post-update health check;
- no automatic reboot is performed;
- Restore config snapshot is visually distinct from package rollback and states that it restores only validated allowlisted configuration;
- restore reports validation failures instead of attempting partial extraction;
- all Safe Update and restore operations use the existing single-mutation gate so they cannot overlap service, Docker, firewall or backup changes.


## v0.9 Fleet UX

- Fleet is a separate workspace rather than expanding the Dashboard server rail;
- the top row contains one filter plus local refresh and safe key-profile probe actions;
- four compact counters summarize saved, trusted, healthy and attention-needed profiles;
- the selected-server secret is visually scoped to the selected Fleet row and is cleared after probing;
- Fleet exposes no bulk restart, update, terminate or firewall mutation actions;
- "Open on Dashboard" moves the user into the existing single-server workflow before any broader administration;
- filtering covers server name, endpoint, username, health and operating system.


## v0.10 Terminal polish

- Terminal exposes a compact Quick Commands selector below the normal command editor;
- presets are read-only inspection commands and never execute on selection;
- Insert preset copies the command into the existing editor so the user can review or modify it first;
- manual Run remains the single execution action;
- preset guidance is visible directly under the selector to avoid ambiguity;
- README visuals use the same Electric Blue / Deep Navy / Slate Blue / Ice White product system without pretending to be application screenshots.


## v0.11 Database workspace

- Databases is a dedicated read-only workspace placed next to other host-inspection tools;
- the primary action is a single Refresh databases control;
- the engine table shows engine, version, service state, access state and discovered database count;
- the details pane shows database names only for the selected engine;
- access limitations are stated directly in the workspace instead of prompting for database-specific credentials;
- no SQL editor or mutation action is exposed in this milestone;
- F5 refreshes the Database workspace using the selected server and current session secret.


## v0.12 Fleet history UX

- the Fleet workspace keeps the inventory and probe controls unchanged at the top;
- a dedicated lower history panel appears below the Fleet table rather than adding another navigation destination;
- selecting a Fleet row updates the timeline for that server only;
- Attention is raised locally at 90% CPU, RAM or disk utilization;
- Attention rows sort ahead of healthy rows so current risk is visible first;
- history shows recorded time, status, CPU, RAM, disk, load and a concise reason;
- history clearing requires explicit confirmation and affects local state only;
- the UI states that history is local-only and does not contain session secrets.


## v0.13 Alert Center UX

- Alerts is a dedicated navigation destination next to Fleet;
- the workspace opens on Active alerts so unresolved local incidents are visible first;
- acknowledgement is an explicit action on one selected incident and does not alter remote state;
- the KPI row shows active, acknowledged, threshold-attention and failure totals;
- Open server transfers the user into the existing single-server Dashboard workflow;
- CSV export covers the full local alert history rather than only the visible filter;
- deleted server profiles remain visible in historical alerts as "(deleted profile)" but cannot be opened;
- F5 refreshes the local Alert Center without contacting remote servers.
