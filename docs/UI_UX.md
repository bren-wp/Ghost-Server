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


## v0.14 Fleet Trends UX

- Trends is a dedicated local-only workspace next to Fleet and Alerts;
- the sample selector supports the latest 10, 25 or 50 records per server;
- four summary cards show servers with history, latest-risk count, highest average CPU and highest average disk utilization;
- risk rows sort ahead of healthy rows, then by failures, Attention count and disk peak;
- averages and maxima are calculated only from Healthy or Attention records that contain health metrics;
- failed probes contribute to failure counts but do not artificially lower metric averages with zero values;
- Open server transfers the user to the existing single-server Dashboard workflow;
- CSV export reflects the currently selected sample window and never contacts remote servers.


## v0.15 Persistent terminal UX

- Terminal exposes explicit Connect shell and Disconnect controls instead of silently opening a new SSH connection for every command;
- the session status is visible as Disconnected, Connecting, Connected, Trust required or Connection failed;
- Send is enabled only when the persistent shell belongs to the currently selected server profile;
- changing servers clears the old terminal surface after disposing the old shell so output cannot be mistaken for the new target;
- working directory and normal shell state persist between commands until disconnect;
- Quick Commands remain insert-only and require an explicit Send action;
- terminal output is locally bounded to prevent unlimited WPF TextBox growth;
- Lock session, SSH trust reset, active-profile edit/delete and window close dispose the interactive shell;
- global application shortcuts do not execute behind the add-server or confirmation overlays;
- v0.15 is a line-oriented terminal surface and does not present itself as a full-screen terminal emulator.


## v0.16 Operation safety and navigation cleanup

- top-level sidebar navigation is reduced from 17 visible items to 11;
- every sidebar item uses a fixed-width icon column and aligned label column;
- navigation rows are shorter and the rail is narrower without reducing click clarity;
- Alerts and Trends are entered from Fleet;
- Backup and Tasks are entered from Safe Update;
- Databases and Logs are entered from System;
- grouped child pages keep their parent sidebar item visibly selected;
- F5 follows the visible page instead of assuming one page per sidebar button;
- ComboBox closed state, popup, items and selected state are dark-themed;
- ListView rows and GridView headers use Ghost dark surfaces instead of Windows light defaults;
- CheckBox and scrollbar surfaces are visually integrated into the Ghost palette;
- selected-server remote results must never update a different server after a selection change;
- duplicate refresh protection is scoped by operation generation;
- changing servers, locking the session, editing/deleting the active profile or resetting trust invalidates stale remote work;
- Safe Update, restore and backup cleanup remains targeted to the captured original server profile.


## v0.17 Responsive window and complete UI pass

- Ghost Server launches as a centered normal window and never intentionally enters Windows Maximized/full-screen state;
- initial and fitted sizes are calculated from the available Windows work area and capped below full-screen dimensions;
- the former maximize control is a bounded Fit window action;
- custom-title-bar double-click toggles between comfortable and fitted normal-window sizes;
- the sidebar uses a 198 px full mode and a 72 px icon-only compact mode;
- compact sidebar mode preserves tooltips and hides section labels/footer before content becomes cramped;
- the content header reduces height and title size when space is constrained;
- subtitle, Add server label and status-bar shortcut legend collapse progressively instead of overlapping;
- Dashboard server-list width adapts with the window;
- add-server and destructive-confirmation cards resize to the available window;
- Fleet, Alerts, Trends, Files, Network, Tasks and Security command surfaces wrap instead of clipping;
- Dashboard server identity/actions and session-secret controls wrap into separate rows when required;
- Settings monitoring and profile import/export controls wrap on narrow layouts;
- tables retain their own scrolling where their data columns are wider than the current viewport;
- the minimum supported interactive window remains intentionally bounded so controls stay usable rather than being scaled unreadably.


## v0.18 Dead-code and UI maintainability contract

- XAML names are kept only when code-behind, bindings, element references or template logic actually consume them;
- private UI helpers must have a real C# caller or an explicit XAML event binding;
- responsive-window lifecycle events are part of the XAML wiring audit;
- keyed theme resources must have a consumer and must not remain as abandoned visual variants;
- service methods without any production source reference are treated as dead-code candidates;
- CI performs the audit before restore/build so maintainability regressions fail early;
- dead-code detection is conservative and never auto-deletes source.

- scrollbars must use Ghost dark surfaces and must not expose Windows light arrow buttons or white tracks;
- vertical and horizontal scrollbar paging must remain functional after custom theming;
- every visible sidebar destination uses the same fixed icon slot, glyph box and label offset;
- grouped child workspaces must highlight a real visible parent navigation item and must not require hidden navigation controls.


## v0.19 Accessibility and interaction-performance contract

- every keyboard-focusable Ghost control must expose a visible focus indicator against the dark surface;
- modal workflows must keep Tab and Shift+Tab inside the active overlay;
- non-destructive primary forms may expose a default Enter action, while destructive confirmation must not default to the destructive action;
- closing a modal should restore focus to the invoking visible control when possible;
- server-editor labels must be associated with their corresponding fields;
- compact icon-only navigation must retain explicit automation names;
- status changes important to assistive technology use polite live announcements, while validation failures use assertive announcements;
- ListView and ListBox surfaces use recycling virtualization and content scrolling;
- responsive layout code may update continuously sized modal bounds during resize, but heavier breakpoint-dependent layout rewrites should run only when the breakpoint signature changes;
- Dashboard auto-refresh must not poll SSH while the app is minimized or not visible;
- focus, virtualization and accessibility additions remain subject to the dead-code, XAML-wiring and analyzer gates.


## v0.20 Window-state and local-storage reliability contract

- only normal-window dimensions may be remembered between launches;
- Fit window is a temporary bounded layout and must never become the persisted startup full-screen/maximized state;
- remembered window dimensions are normalized and clamped against the active Windows work area before use;
- resize persistence is debounced and must not write on every resize pixel;
- startup resize events must not persist settings before the existing settings file has loaded;
- pending normal-window size persistence is flushed before close when necessary;
- Settings must expose both Remember normal window size and Reset window size;
- optional backup-folder configuration must support both setting and clearing the path;
- settings, profile and Fleet-history load/save operations are serialized by process-lifetime per-store gates so atomic temp/backup writes cannot overlap and shutdown cannot dispose a gate underneath a late async completion;
- the additional local-state persistence must never store passwords or private-key passphrases.
- Settings must clearly distinguish persisted values from unsaved form changes;
- Save settings stays disabled until a user-visible setting changes and returns to disabled after a successful save;
- navigating away from Settings must not silently discard pending Settings edits when returning to the page;
- immediate actions such as Reset window size must not silently persist unrelated pending form values;
- Settings change events raised during XAML initialization must not create a false dirty state.
- closing the application with unsaved Settings must require an explicit Save, Discard or Cancel choice;
- unsaved-settings confirmation must use Ghost modal styling rather than platform-default light dialog chrome;
- Discard must never copy pending form values into the persisted settings model;
- explicit Settings save must stage form values in a candidate model and update persisted in-memory state only after a successful write;
- Escape on the unsaved-settings overlay maps to Cancel and focus remains trapped in the active overlay;
- window-geometry persistence remains independent from unsaved form persistence.
- application confirmations must use the shared Ghost modal system rather than platform-default MessageBox chrome;
- confirmation modals default focus to Cancel, trap focus, map Escape to Cancel and block background shortcuts;
- dangerous remote mutations use Danger confirmation styling while non-destructive confirmations may use Accent styling;
- closing the window while an async confirmation is pending cancels the confirmation before any close flow continues;
- local UI must not report successful history mutation when persistence failed;
- destructive local-history mutations must roll back in-memory state when their durable write fails.
