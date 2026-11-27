# Roadmap

What's planned or parked, roughly in order. Items on hold are deliberately not in the
shipped apps.

## On hold

### WhatsApp chats on the laptop

**Status: built, switched off** (`WhatsAppEnabled` in `desktop/MainWindow.xaml.cs` and
`WhatsApp.ENABLED` on the phone). Messages are read from WhatsApp's notifications, and
replies use WhatsApp's own notification reply action.

Limits to solve first:
- **No history:** WhatsApp offers no API for it.
- **Short reply window:** replies only work while the chat's notification is still on
  the phone.
- **Format changes:** notification layouts change between WhatsApp versions.

### Unlock the laptop from the phone

**Status: on hold, removed from the apps.** A working prototype existed: a Windows sign-in
add-on (credential provider) that took a password typed on the phone, sent once over the
encrypted Wi-Fi link, with attempt limits. Before it comes back it should be native, signed and
security-reviewed, and ideally use a laptop-held key so no password travels at all.

### Unlock with the Windows PIN

**Status: later.** Windows only accepts a Windows Hello PIN typed into its own PIN box, so
no sign-in add-on can submit one. The only route is the remote-desktop approach: a small
system service that types the PIN onto the lock screen, as TeamViewer and VNC do. It
needs full system rights, so it needs careful design and review first.

## Next

- **iPhone, next stages.** Notifications are in (beta). Then calls (answer, decline, sound,
  over the hands-free profile Nyx already uses), texts and iMessage (Bluetooth MAP: new
  messages and replies, no history), and contacts (Bluetooth PBAP).

- **Signed laptop installer.** Code-sign the Windows package (MSIX) so Nyx installs
  without Developer Mode, and add auto-update.
- **Call sound reliability.** Connect the phone's Bluetooth hands-free link
  automatically when a call starts, instead of using the Use this laptop button.
- **Notification actions.** Reply to messaging-app notifications that offer a reply
  action, beyond texts.
- **Clipboard sharing.** Copy on one device, paste on the other, over the encrypted link.
- **"Send to Nyx" in File Explorer.** A right-click Send to entry (Nyx already accepts files
  on its command line).
- **MMS and RCS.** Pictures and group chats in Texts.
- **Multiple phones.** Pair more than one phone with one laptop and choose which is
  active.
- **Localisation.** Move UI strings into resources for translation.
