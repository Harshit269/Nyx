# Security

Nyx links a phone to a laptop: the phone can lock and shut down the laptop, and the laptop
can read the phone's texts and notifications. Security problems matter here.

## Reporting a problem

Please don't open a public issue for a security problem. Use GitHub's
**Security > Report a vulnerability** on this repository (private advisory). Include what
you found, how to reproduce it, and what an attacker could do with it. Expect a reply within
a week.

## What Nyx protects against

- **Someone else on the same Wi-Fi** can't read messages, send commands, or pose as your
  phone or laptop. Links are mutually authenticated with the pairing secret and every message
  is AES-256-GCM encrypted.
- **A laptop you never approved** can't link over Bluetooth without your tap on the phone.
- **A copied settings file** on the laptop is useless to another Windows account or PC:
  pairing secrets are sealed with DPAPI.
- **Backups:** the phone app's data is excluded from cloud backup and device transfer.
- **Streams** (mirroring, second screen, webcam, microphone, phone sound) each use a fresh
  32-byte key sent inside the encrypted link, AES-256-GCM per frame, with replays dropped.
- **The USB link** listens on the phone's internal address (127.0.0.1) only, reached through
  adb's port forwarding over the cable; it uses the same pairing proof and encryption as Wi-Fi.

## What it doesn't protect against

- Someone who can already run programs as you on the laptop, or who has your unlocked
  phone, can use Nyx as you.
- **"USB quality over Wi-Fi"** (on the Mirror page, on by default) turns on the phone's adb
  over Wi-Fi (port 5555) whenever the phone is plugged in, until it restarts. A computer that
  isn't already authorised still needs you to tap Allow on the phone, but don't leave it on
  when you use public Wi-Fi: turn the switch off there.
- **"Stop asking on the phone"** lets Nyx start screen sharing without Android's question.
  Only your paired laptop can start it, over the encrypted link; press it again to undo.
- Wake-on-LAN packets are unauthenticated by design (the standard has no security); they
  can only wake a sleeping laptop, never unlock it.

## For contributors

- Never commit keys, keystores, passwords or `keystore.properties` (see `.gitignore`).
- Treat everything arriving from the other device as untrusted: check sizes, formats and
  ranges before use, and never pass it to a shell or command line unchecked.
- Never log message contents, phone numbers, keys or passwords.
