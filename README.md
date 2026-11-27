# Nyx

Your Android phone and your Windows laptop, linked. Nyx is named after Pluto's small
moon: the laptop is the planet, the phone is its moon.

No account, no cloud. The phone and the laptop talk to each other directly, over your
own Wi-Fi (encrypted), the USB cable or Bluetooth. Current version: **1.4.1**.

| On the laptop | On the phone |
|---|---|
| Notifications from the phone: everything in the shade, cleared when it's cleared on the phone; mark as read or dismiss from either side; quiet during full-screen games | Lock, sleep, restart or shut down the laptop (Cancel during the 15-second countdown) |
| Texts: read, reply, start new ones, live incoming, mark all read | Wake the laptop from sleep (Wi-Fi wake-on-LAN) |
| Calls: incoming panel, answer, decline, dial, hang up | **Requests to laptop**: what you asked for and how it went, gone when nothing's pending |
| Call sound on the laptop's mic and speakers (Bluetooth hands-free) | **Use as second screen**: extend the laptop's desktop onto the phone, with real touch |
| **Screen mirroring**: scrcpy over USB or Wi-Fi (up to 120 fps, with sound), or Nyx's own Wi-Fi mirror (H.264 video, sound, click to tap) | **Use as webcam** in Meet, Zoom or Teams (via OBS Virtual Camera) |
| **Phone sound on the laptop** (moves there; the phone goes quiet) | **Use as microphone** (via VB-Audio Virtual Cable) |
| Your phone's wallpaper, battery, signal and charging state | The laptop's on/off log (today or the last 7 days) |
| Find phone: ring it at full volume, even on silent | Share any file or photo to the laptop (Share > Nyx) |
| Send files to the phone: drag onto the window, or Send file | Pick **Wi-Fi or the USB cable** from the link pill |
| Pick **Wi-Fi or the USB cable** from the link pill (green dot when linked) | |
| Starts with Windows, lives in the tray (double-click to open) | |
| iPhone notifications too (beta, no iPhone app needed) | |

    desktop/   Windows app: C# .NET 8, WPF (Windows 10 2004+ / Windows 11)
    android/   Phone app: Kotlin, Android 8+, no third-party libraries
    tests/     Laptop test suite (xUnit)
    docs/      The website (GitHub Pages)
    design/    App icon source and the theme toggle demo
    DESIGN.md  The visual system and the rules behind it
    ROADMAP.md What's next, including features on hold

## Build and run

### Laptop

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Try it with sample data, no phone needed:

       cd desktop
       dotnet run -- --demo

3. Install it properly (needed for call sound on the laptop, see below):
   turn on Settings > System > For developers > **Developer Mode**, then

       cd desktop
       powershell -ExecutionPolicy Bypass -File .\register-package.ps1

   and open **Nyx** from the Start menu. Run the same script again to update; your
   pairing and settings are kept.
4. The first time, Windows asks whether Nyx may use the network. Allow it on
   **private** networks: this is how the phone finds the laptop on Wi-Fi.

### Phone

    cd android
    gradlew assembleDebug        (or open the android folder in Android Studio)

Install `app/build/outputs/apk/debug/app-debug.apk`. In the app, work down the
**Set up** card (it disappears once everything is allowed), then switch **Bridge** on.

Android 13+ blocks texts, call log and notification access for apps installed from a
file. If a permission is greyed out: Settings > Apps > Nyx > ⋮ > **Allow restricted
settings**, then allow it. The app's Set up card takes you there.

## Pairing

1. Phone and laptop on the same Wi-Fi, Bridge on in the phone app.
2. The laptop's Overview shows your phone under "On your Wi-Fi". Press **Pair**.
3. Both screens show a 6-digit code. If they match, allow it on the phone.

From then on they link up by themselves whenever both are on the same Wi-Fi, and the
phone app brings the bridge back on its own after a restart or an update. Bluetooth
works too, as a fallback (pick the phone under "Paired phones" and press Connect).

## Security

Your phone can shut down your laptop and read your texts, so every link is locked to
your paired phone:

- **Pairing** uses an elliptic-curve key exchange (P-256 ECDH) with a commitment, and a
  6-digit code both screens show, so someone else on the Wi-Fi can't slip in between.
  Nothing is saved unless you confirm on both sides.
- **Every Wi-Fi connection** starts with a mutual challenge (HMAC-SHA256 over fresh
  nonces). Both sides must prove they hold the pairing secret before any message moves.
- **Every message** after that is AES-256-GCM encrypted with a key unique to the
  connection.
- **Bluetooth** links need your approval on the phone the first time ("Allow this
  laptop?"). A laptop you deny can't ask again for 10 minutes.
- **On the laptop**, pairing secrets are sealed with Windows DPAPI for your account. On
  the phone, Nyx's data is excluded from cloud backup and device-to-device transfer.
- **The phone** only listens on your Wi-Fi network, never on mobile data, and limits
  half-open connections.
- **Everything from the phone is checked** before use. For example, the address handed
  to scrcpy must be a plain IPv4 address, so nothing extra can reach its command line.
- The laptop keeps a short activity log (`%LOCALAPPDATA%\Nyx\nyx.log`) with events and
  reasons only: never message contents, numbers, keys or passwords.

See [SECURITY.md](SECURITY.md) to report a problem.

## Wi-Fi or the USB cable

Tap the link pill (top right on the laptop, next to light/dark; top right on the phone) and pick
**Wi-Fi** or **USB cable**. The USB link goes through adb's port forwarding to a door on the
phone's internal address only (nothing on Wi-Fi or mobile data can reach it), with the same
pairing proof and encryption as Wi-Fi. While you've picked USB, Nyx stays on the cable and
relinks through it whenever it's plugged in. It needs scrcpy's adb and USB debugging.

## Optional extras

Nyx needs nothing but its two apps for notifications, texts, calls, files and Wi-Fi mirroring.
One free extra adds more, only if you want it:

| Extra | What it's for | Where | Get it |
|---|---|---|---|
| **scrcpy** | Mirror over USB, "USB quality over Wi-Fi", and the one-time "Keep payment apps working" fix | Laptop | [GitHub releases](https://github.com/Genymobile/scrcpy/releases/latest) or `winget install Genymobile.scrcpy` |

## Sending files

- **Laptop to phone:** drag files onto the Nyx window, press **Send file** (Overview or the
  tray panel), or run `Nyx.exe photo.jpg`. They land in the phone's `Download/Nyx`.
- **Phone to laptop:** share any photo or file and pick **Nyx**, or press **Send files to
  laptop** in the app. They land in the laptop's `Downloads\Nyx`.

Files travel in chunks over the same encrypted link, up to 4 GB each. The receiver keeps a
file only if its size and SHA-256 match, cleans its name (no folders, no reserved names) and
never opens it by itself.

## iPhone notifications (beta)

No app on the iPhone: Nyx reads notifications through Apple's Notification Center Service
over Bluetooth LE, the way smartwatches do.

1. Windows Settings > Bluetooth & devices > Add device > Bluetooth, with the iPhone's
   Bluetooth settings open; confirm the code on both.
2. On the iPhone: Settings > Bluetooth > (i) next to the laptop > **Share System Notifications**.
3. Nyx > Settings > **Connect iPhone**.

iPhone notifications join the same list as Android ones, and dismissing one on the laptop
clears it on the iPhone where the iPhone allows it. While no Android phone is linked, the
top-bar pill shows the iPhone with the green Online dot, and the pill's link card lists the
iPhone too. What else an iPhone can and can't do with Nyx is in [docs/iphone-plan.md](docs/iphone-plan.md). The protocol code is tested against
Apple's specification; the Bluetooth connection still needs testing on real iPhones.

## Call sound on the laptop

Answering, declining and dialling always work, because the phone does them. Moving the
call's sound to the laptop uses Windows' Bluetooth hands-free support:

- Nyx must be installed with `register-package.ps1` (Windows only allows this for an
  installed app).
- The phone must be connected to the laptop over **Bluetooth** (Nyx's own link can be
  Wi-Fi), with "Phone calls" allowed for the laptop in the phone's Bluetooth settings.
- Microsoft Phone Link must not be holding the link: turn off Settings > Bluetooth &
  devices > Mobile devices.

Then press **Use this laptop** on the Calls page.

If it still says the laptop can't reach the phone over Bluetooth, check whether Microsoft
Phone Link can put a call through the laptop. If Phone Link can't either, the laptop's
Bluetooth isn't accepting the connection, and no app can fix that. Things to try:
untick "Allow the computer to turn off this device to save power" for the Bluetooth
adapter in Device Manager, restart, and install the newest Bluetooth driver from the
laptop maker. Some Wi-Fi/Bluetooth combo chips (seen on a MediaTek MT7922) don't answer
connection requests from a paired phone at all.

## Screen mirroring

The Mirror page uses [scrcpy](https://github.com/Genymobile/scrcpy). If it isn't
installed, **Install scrcpy** sets it up with winget. Then:

- **USB:** turn on USB debugging on the phone, plug in, press Mirror over USB.
- **Wi-Fi, no cable (Android 11+):** turn on Developer options > Wireless debugging. The
  first time, tap "Pair device with pairing code" and type its port and code into Nyx's
  Pair boxes. After that, Mirror over Wi-Fi finds the phone by itself (Android changes the
  port every time; Nyx looks it up over the network).
- **Android 10 and older:** "Use the cable once instead" switches on the classic Wi-Fi mode
  (once per phone restart).

**Nyx's own Wi-Fi mirror** (no developer options): press Mirror over Wi-Fi and the phone asks
one question, "Start?" (Android 14+ always shares the whole screen, so there's no app picker).
With laptop control on, the question pops up by itself; otherwise tap Nyx's notification.
To skip the question for good, plug the phone in by USB once and press **Stop asking on the
phone** (Android's own setting, set over adb; press again to undo). Only your paired laptop
can start it, over Nyx's encrypted link.

**USB quality over Wi-Fi** (on by default, Mirror page): whenever the phone is plugged in,
Nyx turns on its Wi-Fi debugging (port 5555, until the phone restarts). Mirror over Wi-Fi then
opens scrcpy over Wi-Fi: up to 120 fps, notifications stay visible (Android hides them during
normal screen sharing), Spotify's sound comes through, and the phone asks nothing. The first
time, tick "Always allow from this computer" on the phone. Without it, Nyx's own mirror is used.

The picture is real video: the phone's hardware H.264 encoder (720x1600, up to 120 fps) and
Windows' built-in decoder in low-latency mode, 100+ fps while things move. If
either side can't do video, it falls back to a JPEG per frame.
The phone's screen stays on (dimmed) while mirroring, because Android ends screen sharing when
the phone locks. Turning the phone sideways turns the window too.

With sound, the sound *moves* to the laptop: the phone's media is muted while it streams and
comes back when it ends (the window's speaker button sends it back to the phone). Sound that
Android can't capture stays on the phone and the window says so: Spotify plays through
Android's low-latency path, which no app can capture. For Spotify on the laptop, use
Spotify's own "Connect to a device", or Mirror over USB with sound.

## Phone as a webcam

Use the phone's camera in Google Meet, Zoom, Teams, Discord or anything else that takes a
webcam, over Wi-Fi, so you can move around.

1. Calls only see cameras Windows knows about, so Nyx shows the phone in the free
   **OBS Virtual Camera** that [OBS Studio](https://obsproject.com/download) installs. OBS
   itself doesn't need to be running.
2. Mirror > **Use phone camera**, or on the phone **Use as webcam**.
3. In the call's camera menu, pick **OBS Virtual Camera** (Meet: the arrow next to the camera
   button). Calls remember it.

On the phone: **0.5×** switches to the ultra-wide lens where there is one, **Flip** to the
front camera, **Stop** ends it. Go home or open another app and it keeps filming in a small
picture-in-picture window. Each session gets a fresh AES-256-GCM key sent inside Nyx's
encrypted link. Hold the phone sideways for the full width of the call's picture; upright, the
whole picture is shown with bars at the sides (or filled, with the Mirror page's switch).

## Phone as a microphone

Talk into the phone in a call (with Android's echo and noise cancelling), on its own or with
the webcam (the **Mic** button on the phone's webcam screen).

1. Calls only see microphones Windows knows about, so Nyx plays the phone's voice into the
   free [VB-Audio Virtual Cable](https://vb-audio.com/Cable/): install it (run
   `VBCABLE_Setup_x64` as administrator, then restart).
2. Mirror > **Use phone mic**, or on the phone **Use as microphone**.
3. In the call, pick **CABLE Output (VB-Audio Virtual Cable)** as the microphone.

The phone shows a notification with **Stop** while it's in use, and keeps going while you use
other apps. 48 kHz mono, 20 ms packets, each sealed with a fresh session key.

## Phone audio on the laptop

Mirror > **Play phone audio** (or on the phone, **Play phone audio on laptop**): the phone's
music, videos and games play through the laptop's speakers or headphones, over Wi-Fi.
Android 10+ captures what apps play after the same "Start sharing?" question as screen
sharing; apps can opt out (calls and some streaming apps do), and Spotify's sound can't be
captured at all. While it streams, the phone's own speaker is muted, so the sound plays in
one place. 48 kHz stereo, sealed with a
fresh key per session. The phone shows a notification with **Stop** while it's on.

## Phone as a second screen

Extend the laptop's desktop onto the phone, like a second monitor, and use it like a
touchscreen: the phone's fingers reach Windows as real touch input, so swipe scrolls, pinch
zooms and long-press right-clicks, and the laptop's own mouse pointer stays where it is.
**Stop** on the phone ends it; the laptop takes its windows back and switches the virtual
screen off again.

1. Windows needs an extra display to extend onto. Install the free, open-source
   [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
   (Mirror > Get the virtual display) and add a display in it. A resolution with the
   phone's shape (for example 2400×1080) fills the phone best.
2. Mirror > **Extend to phone**, or on the phone **Use as second screen**. If Windows went
   back to one screen (it does after sleep or a restart), Nyx extends it again.
3. The new screen starts empty, like any new monitor. On the phone, tap **Windows** to
   bring one of the laptop's windows over (tap one marked ✓ to send it back), or drag a
   window off the edge of the laptop screen, or press Win+Shift+→ on it.

The phone must be on the Wi-Fi link. Every session gets a fresh AES-256-GCM key sent
inside Nyx's encrypted link; frames are JPEG, about 20 a second, and an unchanged screen
isn't sent again. Touches only ever land on the extra display.

## Waking the laptop from the phone

**Wake** sends a Wake-on-LAN signal to the laptop's Wi-Fi. The phone's **Requests to laptop**
card shows it waiting, then "The laptop is awake" or, after 40 seconds, that it didn't wake.
That card also follows Lock, Sleep, Restart and Shut down (with Cancel during the 15-second
countdown) and files you send, and hides when nothing is pending.

Windows only wakes from Wi-Fi when its Wi-Fi card's **Wake on Magic Packet** is on. Nyx's
Settings shows whether it is, and **Let the phone wake this laptop** turns it on (Windows asks
for permission; the Wi-Fi drops for a few seconds). The phone sends the wake signal both to the
network's broadcast address and to the laptop's own address, which gets through more often on
Wi-Fi. On battery, Modern Standby may switch the Wi-Fi off during long sleeps; plugged in, it
stays reachable. (Turning just the screen off doesn't help on these laptops: Windows goes into
standby anyway.)

Some laptops (Modern Standby with some Wi-Fi cards) never list the Wi-Fi card as able to wake
them; then Wake works only while the laptop is awake or after it wakes for another reason.

## Tests

    dotnet test tests/Nyx.Tests        (laptop: 112 tests)
    cd android && gradlew testDebugUnitTest lintRelease   (phone: 21 tests + lint)

Both suites check the same encryption "known answers", generated independently with
Python's cryptography library, so phone, laptop and reference agree byte for byte. The
laptop tests also run the full Wi-Fi handshake against a separately written fake phone
that misbehaves in every way it can: wrong key, wrong id, forged proof, garbage,
silence, tampered frames and oversized lines.

One laptop test listens on Nyx's discovery port, so close Nyx before running the suite.

## Releasing

**Downloads.** `build-release.ps1` runs every test and builds the two downloads into
`dist/release/`: `Nyx-windows-x64.exe` (one self-contained exe, nothing else to
install) and `Nyx-phone.apk`.

**Automatic.** Publish a release on GitHub (tag like `v1.4.1`) and the *Release* workflow
builds both and attaches them. The *CI* workflow runs all tests on every push.

**Phone signing.** Phones only install signed APKs. Create a release key once (see
`android/keystore.properties.example`), keep it private, and either:
- locally: copy the example to `android/keystore.properties` (git-ignored), or
- on GitHub: add the secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_STORE_PASSWORD`,
  `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`.

Never commit the keystore or its passwords. Keep a backup: without it you can't ship
updates that install over the old app.

**Website.** `docs/` is a one-page site for GitHub Pages: Settings > Pages > Deploy from a
branch > `main`, folder `/docs`. Its download buttons always point at the latest release.

**Windows signing.** `Nyx-windows-x64.exe` isn't code-signed yet, so Windows SmartScreen
asks people to confirm the first time (see ROADMAP.md).

## Known limits

- Wake works from sleep only; a fully shut-down laptop has its Wi-Fi off too. The
  laptop's network adapter must allow "wake on magic packet".
- Some routers stop devices talking to each other ("client isolation", common on guest
  and campus Wi-Fi). Nyx falls back to Bluetooth there.
- Texts are SMS only; MMS and RCS chats aren't included.
- Some phone makers (Xiaomi, Oppo, Vivo, Realme) close background apps: set Nyx's
  battery use to "No restrictions".

Icons are from [Feather](https://feathericons.com) (MIT licence).
