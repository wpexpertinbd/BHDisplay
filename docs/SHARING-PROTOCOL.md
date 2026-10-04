# BHDisplay keyboard & mouse sharing — protocol v1

Two computers on the same LAN, each running BHDisplay (macOS) or BHDisplay for Windows. Either computer's
keyboard and mouse can control the other: when the pointer is pushed past the configured screen edge, input
is captured locally and replayed on the other computer until the pointer comes back. The monitor input is
**never** changed by this — that stays a manual action (menu / shortcut).

Design goals: nothing can be typed into a computer by a device the user has not explicitly paired; a lost
connection never leaves a key or button stuck; both ends are small, dependency-free implementations
(CryptoKit on macOS, `System.Security.Cryptography` on Windows).

All integers are **big-endian**. `||` is concatenation.

## 1. Ports

| Purpose   | Transport | Port  |
|-----------|-----------|-------|
| Session   | TCP       | 24860 |
| Discovery | UDP broadcast to 255.255.255.255 | 24861 |

## 2. Identity

Each installation creates once, and keeps, a long-term **P-256 ECDSA** key pair and a random 16-byte
**device id**. The identity *fingerprint* is `SHA-256(identity public key, X9.63 uncompressed, 65 bytes)`.
A peer is **paired** when its fingerprint is in the local paired list. Private keys never leave the device
(macOS: file `~/Library/Application Support/BHDisplay/identity`, mode 0600; Windows: DPAPI-protected file in
`%LOCALAPPDATA%\BHDisplay`).

## 3. Discovery (UDP, unauthenticated, informational only)

Every 2 s each device broadcasts:

```
"BHDS1" (5) || device id (16) || tcp port u16 || fingerprint[0..8] (8) || name length u8 || name UTF-8 (≤ 64)
```

A receiver only learns *where* a device is. Nothing in a discovery packet is trusted: the session
handshake authenticates everything, and an unpaired device can do nothing but ask to pair.
Of two devices, the one with the **lower device id (byte-wise)** opens the TCP connection (the *dialer*);
the other only accepts (the *listener*). This avoids duplicate connections.

## 4. Framing

Every frame is `length u32 || body`. `length` counts the body only and must be 1 … 1 048 576; anything else
closes the connection.

## 5. Handshake (plaintext frames)

1. Dialer → listener: **HELLO**
2. Listener → dialer: **HELLO**

```
HELLO = "BHDS" (4) || version u8 (=1) || device id (16) || identity public key (65) ||
        ephemeral P-256 ECDH public key (65) || random nonce (32) || name length u8 || name UTF-8 (≤ 64)
```

Both compute the transcript hash

```
T = SHA-256( "BHDS-v1" || HELLO_dialer || HELLO_listener )
```

3. Each side sends **AUTH** = ECDSA-P256-SHA256 signature, raw `r || s` (64 bytes), over
   `"BHDS-auth" || role u8 || T` with its identity key (role 0 = dialer, 1 = listener).
   Each side verifies the peer's AUTH with the identity key from the peer's HELLO. Failure closes the connection.

4. Session keys: `Z = ECDH(own ephemeral, peer ephemeral)`,
   `K = HKDF-SHA256(ikm = Z, salt = T, info = "BHDS-v1 keys", L = 64)`;
   `k_dialer→listener = K[0..32]`, `k_listener→dialer = K[32..64]`.

5. **Pairing code** (only shown when the peer is not yet paired):
   `P = HKDF-SHA256(ikm = Z, salt = T, info = "BHDS-v1 pair", L = 4)`, code = `u32(P) mod 1 000 000`,
   shown as 6 digits. Both screens show the code; the user confirms on **both** computers that they match.
   A man in the middle would hold two different transcripts and therefore two different codes.

The ephemeral keys are discarded after step 5 (forward secrecy).

## 6. Encrypted frames

After AUTH, every frame body is `AES-256-GCM(key for this direction, nonce, plaintext)` = ciphertext || 16-byte tag,
with `nonce = 0x00000000 || sequence u64`, the sequence starting at 0 per direction and incrementing by one per
frame. A frame that fails to decrypt closes the connection. Plaintext = `type u8 || payload`.

## 7. Messages

| Type | Name | Payload | Rule |
|------|------|---------|------|
| 0x01 | PING | — | every 2 s; no frame for 6 s → disconnect |
| 0x02 | PONG | — | |
| 0x05 | PAIR_CONFIRM | — | user accepted the code on the sender |
| 0x06 | PAIR_REJECT | — | user rejected; close |
| 0x10 | ENTER | edge u8, position f32 (0…1) | sender starts controlling the receiver; the receiver's pointer appears at its `edge` (0 left, 1 right, 2 top, 3 bottom) at `position` along it. Edge **4 = take over**: the pointer is not moved and is not handed back at an edge (used when the sender's screen is not visible) |
| 0x11 | LEAVE | edge u8, position f32 | the controlled pointer left through the receiver's-side edge back toward the controller; the controller stops capturing and resumes locally |
| 0x20 | MOVE | dx i16, dy i16 | relative pointer motion, screen points |
| 0x21 | BUTTON | button u8 (1 left, 2 right, 3 middle, 4 back, 5 forward), down u8 | |
| 0x22 | SCROLL | dx i16, dy i16 | 120 = one wheel notch; dy > 0 = wheel away from the user |
| 0x30 | KEY | usage u16, down u8 | USB HID Keyboard page (0x07) usage ID |
| 0x31 | RELEASE_ALL | — | release every key and button this peer pressed |
| 0x40 | CLIPBOARD | UTF-8 text (≤ 256 KiB) | sent when control moves to the other computer and the text changed |
| 0x50 | MONITOR_PORTS | mac port u8, other port u8 | the monitor's VCP 0x60 values for "this Mac" and "the other computer", sent by the Mac |
| 0x51 | MONITOR_SHOWS | input u8 | the shared monitor now shows this input (VCP 0x60 value); whoever switches it, or notices a change, tells the other |
| 0x52 | SWITCH_REQUEST | input u8 | ask the other computer to switch the shared monitor to this input (it may first need to turn its own output to the monitor back on) |
| 0x53 | SWITCH_ACCEPTED | input u8 | sent at once by the receiver of SWITCH_REQUEST: "I'm doing it" — the asker then must NOT switch by itself (the receiver may need several seconds to turn its output on). No answer within 2 s (older version) → the asker switches itself |

**Input-carrying messages (0x10–0x40) are ignored unless the peer is paired.**
Unknown types are ignored (forward compatibility).

## 8. Shared-monitor mode

For two computers sharing one monitor. Each keyboard and mouse always works on its own computer and moves to
the other only when its pointer is pushed past the edge that faces the other computer; what the shared monitor
shows never hands input over by itself, and mouse movement never switches the monitor input.

- While the shared monitor shows the **other** computer, its area on the Mac's desktop *is* that computer: the
  Mac's pointer crossing onto it starts controlling the other computer, and the other computer's pointer arrives
  beside it on the Mac's own screens.
- While it shows the **Mac**, the other computer is beyond the outer edge of the Mac's desktop on the shared
  monitor's side; its pointer enters and leaves there. The Mac does not cross into the other computer then (it
  is not visible).
- The most recently moved mouse wins: crossing over ends any control the other side had on this computer.
- When the monitor changes computers, any crossed-over pointer is sent home on both sides.

ENTER edge 4 (take over) is reserved by earlier builds and is no longer sent.

## 9. Safety rules

- On disconnect, on LEAVE, and on RELEASE_ALL the receiving side releases every key and button it pressed
  on behalf of the peer. A capturing side that loses its connection stops capturing immediately.
- Events a side injects are tagged and ignored by its own capture (no feedback loops).
- Receiving ENTER while capturing makes the receiver stop capturing first (the last computer touched wins).
