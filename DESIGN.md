# Nyx design notes

A dashboard for your phone, in a Nothing-style minimal look: black, white and
greys, one red for anything live or urgent, dot-matrix type for names and
numbers, and motion that settles rather than snaps. Layout follows a fleet
dashboard (Haulix): an icon rail, a top bar with live status, then rounded tiles.

## Layout

- **Icon rail** (76 px, left): NYX mark in dot matrix, six round icons (Overview,
  Notifications, Messages, Calls, Screen mirror, Settings). The theme button
  is in the top bar. The selected icon sits on an ink-filled puck that glides between
  items. Red count badges sit on Notifications and Messages.
- **Top bar**: the page name in dot-matrix capitals with today's date under it.
  On the right is a live call pill (only while there is a call) and the phone
  pill (name plus "Wi-Fi" or "Bluetooth"), whose red dot pulses while the link is
  live, then the theme button: a 7x7 dot sun that redraws itself into a moon in
  a diagonal sweep (`DotIcon`), with the sun's centre dot in red.
- **Pages** are tiles with a 20 px radius on the canvas. The Overview grid holds
  Phone (connection and link facts), Today (dot-matrix count and a 12-hour dot
  chart), Quick actions, Unread texts and Latest notifications.
- Lists are rows inside tiles, never cards inside cards.

## Tokens

Colours live in `desktop/Colors.Light.xaml` and `desktop/Colors.Dark.xaml`.
Everything else uses `DynamicResource`, so swapping one dictionary repaints the
whole app. Nothing in C# names a colour value.
`android/.../MainActivity.kt` mirrors the light values.

| Token | Light | Dark | Use |
|---|---|---|---|
| Canvas | #ECECEC | #000000 | window floor |
| Rail | #E4E4E4 | #000000 | icon rail |
| Tile / Tile2 | #FFFFFF / #F4F4F4 | #101010 / #191919 | tiles / inputs, avatars, selected rows |
| Line / LineStrong | #E3E3E3 / #CDCDCD | #232323 / #353535 | hairlines, switch track |
| Ink / Ink2 / Ink3 | #0A0A0A / #555555 / #6E6E6E | #F2F2F2 / #A6A6A6 / #8A8A8A | text levels |
| OnInk | #FFFFFF | #0A0A0A | text on ink buttons and sent bubbles |
| Red | #D71921 | #EB2F37 | live link, unread, incoming call, decline, newest chart column |
| DotOff | #DEDEDE | #272727 | unlit dot-matrix dots |

Primary actions are **ink**: black in light mode, white in dark. Red is never
decoration. If it is red, it is live or needs you.

## Type

- Segoe UI Variable Text for UI (13–14), Display for headings (17, 24).
- `DotText` (desktop/DotMatrix.cs) is a hand-built 5x7 dot-matrix face for page
  titles, the NYX mark, the Today count and the version number. It is used only
  for short capitals and numbers, never for running text.
- Tile labels are 11 px small caps in Ink3.

## Motion

All motion uses an exponential or back ease-out and is skipped when Windows
animations are off.

- **Theme switch:** a 380 ms crossfade (ease in-out). A still picture of the old
  theme is laid over the window, every colour switches underneath, and the
  picture fades out, so text stays readable throughout. The title bar flips at
  the midpoint.
- **Pages:** tiles rise 18 px and fade in, 45 ms apart.
- **Rail puck and theme thumb:** glide with a slight overshoot.
- **New notifications and messages:** slide down into their lists.
- **Dot-matrix text:** lights up in a diagonal sweep when its value changes.
  The dot chart's columns rise one after another.
- **Buttons:** tint on hover and press to 95%, then spring back.
- **Looping motion:** only the red link halo and the ringing call dot loop,
  and only while they are true.

## Rules kept

- Every control has hover, pressed, focused (red ring) and disabled states.
- Every empty tile says what will appear and how to get it.
- No modal dialogs. Errors show where the problem is.
- No gradients, glass, glow or emoji icons. Icons come from Feather (MIT).
- Sample data appears only in `--demo`, and the Phone tile says so.

## Developer flags

`--demo` loads sample data. `--theme light|dark|auto` overrides the saved
theme for one run. `--page N` opens on page N (0 = Overview). The theme choice
is saved to `%APPDATA%\Nyx\settings.json`; pairings live in `pairs.json` and
other preferences in `prefs.json` next to it. `--minimized` starts in the
taskbar (used by "Start Nyx with Windows").
