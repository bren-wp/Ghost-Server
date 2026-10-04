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
