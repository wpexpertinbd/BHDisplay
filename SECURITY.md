# Security

BHDisplay sends commands to your monitor, so it is built to send only what you asked for.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Email
**benjamin dot biswas at gmail dot com** (or contact
[BiswasHost](https://www.biswashost.com)) with details and steps to reproduce.
You'll get a response as soon as possible.

## What BHDisplay does — and doesn't — do

- **No network access.** No telemetry, no update checks, no accounts. The only links are the
  About/footer links, which open in your browser when you click them.
- **No privileges.** No admin password, no helper tool, no kernel extension, no Accessibility
  permission. Global shortcuts use the Carbon hot-key API, which needs none.
- **Talks only to the monitor**, over DDC/CI on the display cable, through the Apple Silicon
  display controller (private IOKit I²C functions declared in `Sources/Bridge.h`).
- **Monitor replies are untrusted input.** Every reply is checked for the expected source
  address, length, opcode, echoed feature code and checksum before it is used.
- **Saved preferences are untrusted input.** The two remembered input ports are accepted only
  if they are one of the monitor's real inputs; anything else is ignored and re-learned.
- **Commands go to the monitor the window describes.** The monitor's identity (EDID manufacturer
  and product) is read through the same DDC channel the commands use, the window only shows that
  display, and queued writes are dropped if a different monitor appears before they are sent.
- **Destructive actions need intent.** Factory reset asks for confirmation and names the monitor.
  On the command line, raw writes need `--unsafe`, and reset / power-mode codes also `--really`.

## Distribution

Releases are ad-hoc signed with the Hardened Runtime but **not notarized** (no paid Apple
Developer account). Build from source (`./build.sh`) if you prefer to verify what you run.
The `.pkg` installs only `/Applications/BHDisplay.app`; its single pre-install script quits a
running copy of BHDisplay (matched by its full path) so the update replaces it.
