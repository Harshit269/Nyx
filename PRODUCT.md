# Product (Nyx, formerly PhoneBridge)

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop (WPF, .NET 8, Windows 10 2004+ / 11) paired with an Android companion app (Kotlin, Android 8+). Not web.

## Users

Anyone with an Android phone and a Windows laptop (public release). They keep the laptop open for hours while working or studying, with the phone in a bag, on a charger or across the room, and want to see and answer what the phone is doing without picking it up. Phones vary widely by brand, so setup has to work without help.

## Product Purpose

Bring an Android phone's notifications, SMS, contacts and calls to the laptop, and let the phone lock, sleep, restart, shut down and wake the laptop and see its on/off log. The link is automatic over Wi-Fi once paired, with Bluetooth as a fallback; screen mirroring runs through scrcpy. Success: the user stops reaching for their phone while at the laptop.

## Positioning

A direct alternative to Microsoft Phone Link. Every Android brand works the same way, because the link is direct (local Wi-Fi or Bluetooth) to Nyx's own phone app, encrypted with a key the two devices made together, with no account and no cloud relay.

## Constraints

- Sample data appears only in `--demo` mode and is labelled as sample.
- Call audio on the laptop needs a package identity (register-package.ps1); this is the least certain feature.
- SMS only: MMS and RCS are not supported. Hang up from the laptop needs Android 9+.
- Status: v0.3. The Wi-Fi link was tested end to end on an emulator, not yet on a real phone.
- Wake-on-LAN only wakes from sleep, never from full shutdown.

## Accessibility

Keyboard reachable with visible focus; text contrast at least 4.5:1; motion skipped when Windows animations are off. Light and dark themes, plus a mode that follows Windows.
