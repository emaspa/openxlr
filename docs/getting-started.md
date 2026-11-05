# Getting started with OpenXLR

Set up one microphone, one listening output and one app first. Once you can
hear the mixer, you can give a call or stream its own mix.

## Install and open the app

1. Follow the [installation steps for your distribution](../README.md#install).
   For the experimental SteamOS or Bazzite package, follow the
   [Flatpak setup](flatpak.md#install-on-steamos-and-bazzite).
2. On a native package with the OpenXLR systemd user service, start it
   from your regular desktop account.

   ```sh
   systemctl --user enable --now openxlr-daemon
   ```

   Follow the package instructions for loading the WirePlumber rules and
   reconnecting any supported Elgato device after installation.
3. Open **OpenXLR** from the application menu. The **Welcome guide** opens
   the first time the mixer window is shown with a fresh configuration.
   Use **Next** and **Back** to move through the tour, and the buttons
   inside each step to jump to the relevant controls or manual section.
   You can follow along in the mixer while the guide stays open.
   **Finish**, **Skip guide**, or closing the guide dismisses the automatic
   welcome. Open **Options → Support → Welcome guide** to take it again.

## Set up audio

1. **Microphone.** A connected Elgato interface appears in the header.
   For another microphone or headset, open **Options → Audio → Input
   bindings** and pick it for **XLR 1**. Speak into the microphone and check
   that its meter moves. Use [Mic setup](manual.md#mic-setup) on the strip
   to check the input level and listen to a short take.
2. **Headphones or speakers.** Open **Devices** in **Monitor**, select
   the output you want to hear and use **Monitor A** for its feed. Start
   with low volume. [Monitor settings](manual.md#monitor) explains how to
   give a second output a different mix.
3. **One app.** Play some audio, find the app under **Applications** and
   choose a channel, such as Music. You can also select **OpenXLR Music**
   as the app's playback device in your desktop sound settings. Change
   the Music fader for Monitor A and check that what you hear follows.
   **Flow** in the header shows the live route.
4. **Stream or call.** Choose **OpenXLR Stream** as an input in OBS or
   **OpenXLR Chat** as the microphone in a call app. Adjust those mixes
   separately from Monitor A. If call playback enters the Voice Chat
   channel, mute its send to Chat to keep other callers from hearing
   themselves.

The [manual](manual.md#concepts) explains channels, mixes, virtual
microphones and monitor feeds. After the basics, try the microphone
[EQ and plugins](manual.md#plugins), save a [profile](manual.md#profiles),
or change [appearance and control sizing](manual.md#skins).

## If you cannot hear anything

- Check that the source is playing, that its channel meter moves, and that
  the channel and Monitor A are unmuted. Check the selected monitor output
  and its feed.
- If OpenXLR cannot connect to the native user service, check its status.

  ```sh
  systemctl --user status openxlr-daemon
  ```

  For a Flatpak or source build, use the launch instructions for that
  installation. The [troubleshooting guide](manual.md#troubleshooting)
  covers missing devices and daemon problems.
- **Options → Support → Collect diagnostics** saves an archive to attach
  when reporting a problem.
