# Security

BHDisplay sends commands to your monitor, so it is built to send only what you asked for.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Email
**benjamin dot biswas at gmail dot com** (or contact
[BiswasHost](https://www.biswashost.com)) with details and steps to reproduce.
You'll get a response as soon as possible.

## What BHDisplay does — and doesn't — do

- **No internet access.** No telemetry, no update checks, no accounts. The only links are the
  About/footer links, which open in your browser when you click them.
- **Keyboard & mouse sharing** (off on the Mac until you turn it on) uses the local network only:
  TCP 24860 and UDP 24861 broadcast discovery. The protocol is in `docs/SHARING-PROTOCOL.md`:
  P-256 identities, a commit-then-reveal handshake so the 6-digit pairing code can't be steered by
  someone in between, ECDH + HKDF session keys, AES-256-GCM records. Only a paired, authenticated
  peer's input is replayed; a pairing is accepted only for 2 minutes after a person chooses
  *Pair a new computer…* on that computer, and the Pair button has no keyboard shortcut. On the
  Mac the identity key and paired list live in the Keychain. Discovery and handshakes are rate-
  and size-limited. Password-manager (concealed) clipboard items are never sent.
- **No admin privileges.** No helper tool, no kernel extension. Sharing needs the Accessibility
  permission (to read and type keys); global shortcuts use the Carbon hot-key API, which needs none.
- **Windows:** the identity key is DPAPI-protected for the current user. Known limit — like any
  Windows app's data, other programs running as the same user could read it; Windows has no
  per-app equivalent of the Mac Keychain.
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

Mac releases are signed (self-signed certificate, Hardened Runtime) but **not notarized** (no paid Apple
Developer account). Build from source (`./build.sh`) if you prefer to verify what you run.
The `.pkg` installs only `/Applications/BHDisplay.app` and has two small scripts: **preinstall**
quits a running copy of BHDisplay (matched by its exact path) so the update replaces it, and
**postinstall** reopens it as the user logged in at the screen — never as root, and not at all
when nobody is logged in. Both act only when installing to the startup disk, and postinstall opens
the path only if it is a real folder (not a symlink) whose bundle ID is `com.biswashost.bhdisplay`.
Known limit: with several users logged in (fast user switching), other users' copies are quit too
and come back at their next login.

The Windows app is not code-signed (SmartScreen asks once). It installs per user under
`%LOCALAPPDATA%\Programs\BHDisplay` without admin rights, loads Windows DLLs only from System32,
and `BHDisplay.exe --selftest` checks its encryption against known answers on that PC.
