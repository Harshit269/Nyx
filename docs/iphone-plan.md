# iPhone support plan

Nyx was built around the Android companion app. An iPhone can't run a sideloaded companion
app, so everything Nyx does with an iPhone has to go through what iOS itself offers a paired
Windows PC over Bluetooth. This page says what that gets us, page by page, what it can't do,
and which Windows APIs each part uses.

## What iOS offers a paired PC

| Profile | What it carries | Transport | Windows API |
|---|---|---|---|
| **ANCS** (Apple Notification Center Service) | Every notification: app, title, subtitle, text, time. Positive/negative action (e.g. *Answer/Decline* on a call, *Clear* on most others). | Bluetooth LE, GATT. The PC is the GATT client; the iPhone is the server. | `Windows.Devices.Bluetooth.BluetoothLEDevice`, `GenericAttributeProfile.GattDeviceService/GattCharacteristic` (subscribe via CCCD, write Control Point, read Data Source) |
| **AMS** (Apple Media Service) | Now playing (title, artist, album, position), play/pause/next/previous/volume. | Bluetooth LE, GATT | Same GATT APIs as ANCS |
| **HFP** (Hands-Free, iPhone is Audio Gateway) | Incoming call ring, caller number, answer/hang up/dial, call audio (SCO). | Bluetooth Classic | Windows' own hands-free stack: `Windows.Media.Devices` / the call audio endpoint that `HandsFree.cs` already drives; call control through the Bluetooth hands-free audio device. Raw RFCOMM to HFP is taken by Windows. |
| **PBAP** (Phone Book Access, iPhone is server) | Contacts and recent calls (incoming, outgoing, missed), read only. | Bluetooth Classic, OBEX over RFCOMM | `Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService` (service id `0x112F`), `Windows.Networking.Sockets.StreamSocket`, our own OBEX + vCard parser |
| **MAP** (Message Access, iPhone is server) | iMessage/SMS: new-message notifications, read recent messages, send. iOS only allows it once the user ticks *Sync notifications/messages* for that PC, and has historically limited history and sending. | Bluetooth Classic, OBEX over RFCOMM (MAS `0x1132`, plus an MNS server on the PC `0x1133`) | `RfcommDeviceService` (client to MAS), `RfcommServiceProvider` + SDP attributes (MNS server so the iPhone can push "new message" events), `StreamSocket` |
| **AVRCP / A2DP** | Media keys and laptop-as-speaker. Windows supports A2DP **sink** only on some builds/drivers; AVRCP controller is not exposed to apps. | Bluetooth Classic | Not exposed to apps. Use AMS (above) for now-playing and controls instead. |

The connection is one Windows Bluetooth pairing (Settings > Bluetooth & devices > Add
device). iPhones derive the LE keys from the classic pairing (cross-transport key
derivation), so one pairing covers both ANCS/AMS (LE) and HFP/PBAP/MAP (classic).

## Page by page

### Overview
- Phone name and the green Online dot in the top-bar pill while ANCS is connected. **(slice 1)**
- Notification count and the latest arrivals, same as Android. **(slice 1)**
- Battery: the **Battery Service** (GATT `0x180F`) is often exposed by iPhones over LE; try it, hide the battery if missing.
- Now playing card and controls through AMS. (later)
- Not possible: wallpaper, signal bars, Do Not Disturb toggle, ring/silent, phone storage.

### Notifications
- All iPhone notifications in the normal list with app name, title, text and time. **(slice 1)**
- Toasts on the laptop for new ones, not for ones already on the phone when it connects or ones that only changed. **(slice 1)**
- Dismiss on the laptop clears it on the iPhone through ANCS *negative action*, when iOS offers one for that notification. **(slice 1)**
- Positive action (e.g. *Mark as read*, *Answer*) where the event flag says the iPhone offers it. (next)
- Not possible: **inline reply** from a notification. ANCS has no reply action for third-party accessories (Apple keeps that to Apple Watch). Replies to texts have to go through MAP.

### Messages
- Through MAP, when the user allows *Sync messages* for the PC on the iPhone: list recent iMessage/SMS, show new ones as they arrive (MNS), send a text.
- Many iOS versions give MAP only new messages, no history, and some refuse sending; Nyx must detect that and fall back to "notifications only" without errors.
- Until MAP lands, texts already show up through ANCS on the Notifications page.

### Calls
- Incoming call ring and caller: ANCS sends an *Incoming call* notification with Answer (positive) / Decline (negative) actions. That gives answer/decline without HFP. (next)
- Recent calls and contacts: PBAP (`telecom/cch.vcf`, `telecom/pb.vcf`). Needs *Sync contacts* allowed on the iPhone.
- Dialling and call audio on the laptop: HFP through Windows' hands-free support. On this laptop's MediaTek MT7922 the hands-free profile has been unreliable, so call audio may not work here even when the rest does; the phone keeps the call in that case.

### Mirror
- **Not possible.** iOS has no screen-sharing API for a third-party Windows app. AirPlay screen mirroring needs an AirPlay receiver (Apple's protocol, licensing and DRM questions) and doesn't give control back. The Mirror page shows "Android only" for an iPhone.
- **Webcam / mic streaming from the phone: not possible** without an app on the iPhone (Continuity Camera is Mac-only).
- **File send: not over Bluetooth.** iOS doesn't accept OBEX Object Push. Possible later routes: a web page served by Nyx on the local network that the iPhone opens in Safari (QR code), or the iPhone's own Share > Save to Files on an SMB share. Both are separate projects.

### Settings
- The iPhone card: connect/reconnect, current state, the setup steps. **(exists)**
- Per-feature switches once MAP/PBAP/AMS exist, each with the matching iPhone toggle explained (*Share System Notifications*, *Sync Contacts*, *Show Notifications* in the iPhone's Bluetooth device page).

## Slices

1. **iPhone as a linked phone** (this change): ANCS notifications in the list and toasts,
   dismiss, the pill shows the iPhone with the Online dot, and the link picker lists the iPhone.
   Android keeps priority in the pill when both are connected.
2. Incoming call card from ANCS (answer/decline via ANCS actions), positive actions.
3. AMS now-playing and media controls on Overview.
4. PBAP: contacts and recent calls on the Calls page.
5. MAP: messages, if the user's iOS version allows it.
6. HFP call audio, as far as the Bluetooth hardware allows.

## Constraints kept

- No app or account on the iPhone, no Apple ID, no Microsoft account.
- Everything the iPhone sends is treated as untrusted: length-capped parsing, malformed packets dropped (see `Ancs.Assembler`).
- Pairing and any driver changes stay with the user (Windows' own pairing dialog).
