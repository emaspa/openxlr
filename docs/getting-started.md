# Getting started with OpenXLR

Start with one microphone, one listening device and one app. Once you
can hear the mixer, you can give a call or stream its own mix.

## 1. Install and open OpenXLR

1. Follow the [installation steps for your distribution](../README.md#install).
   For SteamOS or Bazzite, follow the [Flatpak setup](flatpak.md#install-on-steamos-and-bazzite).
2. If the native package provides the OpenXLR systemd user service, start
   it from your regular desktop account.

   ```sh
   systemctl --user enable --now openxlr-daemon
   ```

   Follow the package instructions for loading the WirePlumber rules and
   reconnecting any supported Elgato device after installation.
3. Open **OpenXLR** from the application menu. The **Welcome guide**
   opens the first time the mixer is shown with a fresh configuration.
   Use **Next** and **Back** to move through the guide at your own pace.
   The buttons inside a step take you to the relevant controls or manual
   section. You can use the mixer while the guide is open.

**Skip guide** is available on every step. Skipping, finishing or closing
the guide dismisses the automatic welcome for this user configuration,
including after updates. To open it again, go to
**Options → Support → Welcome guide**.

## 2. Check your microphone

1. Plug in your microphone. A connected Elgato interface appears in the
   header. For a USB headset, webcam or other microphone, choose its source
   for **XLR 1** under **Options → Audio → Input bindings**.
2. Speak at your normal volume and look for a moving meter on the input
   strip. Check the device mute and the input binding if the meter is still.
3. Use [Mic setup](manual.md#mic-setup) on the microphone strip to
   record a short take and compare your raw and processed voice.

## 3. Hear the mixer

1. Turn down the output volume before testing. In **Monitor → Devices**,
   select the headphones or speakers you want to hear and choose
   **Monitor A** as their feed. The **Headphones** card controls the
   headphone jack's own volume on supported interfaces.
2. Play audio in a browser or music app. Under **Applications**, find the
   app and choose a channel such as **Music**. OpenXLR remembers the
   assignment. You can also choose **OpenXLR Music** as that app's
   playback device in your desktop sound settings.
3. In **Submixer**, find the Music row and move its fader in the
   **Monitor A** column. The volume on the selected output should follow.
   The same Music channel can have a different level in other mixes.

To give a second output its own blend, select it under **Monitor → Devices**
and choose **Monitor B** or another monitor mix as its feed. Use **Flow**
in the header to see which mix feeds which device. [Monitor settings](manual.md#monitor)
has examples for headphones and speakers.

## 4. Send a separate mix to a call or stream

Choose **OpenXLR Chat** as the microphone in a call app, or
**OpenXLR Stream** as an audio input in OBS or a recording app. Adjust the
corresponding column in the submixer. If call playback is routed to the
**Voice Chat** channel, mute its send in the **Chat** column so callers
do not hear themselves. [Channels and mixes](manual.md#concepts) explains
how these virtual microphones work.

## 5. Keep a setup you like

- Save a [profile](manual.md#profiles) from the header so you can recall
  your mixer setup later.
- Open [microphone EQ or effects](manual.md#microphone-eq) on a channel
  and use one recording in Mic setup to compare changes.
- Set [appearance and control sizing](manual.md#skins) under
  **Options → Appearance**. Use **Arrange** in the header to move
  sections, or **Pop out** to put the submixer in a separate window.
- Set [startup and tray behaviour](manual.md#autostart) under **Options → General**.
  **At login** controls whether audio and the app start when you sign in.
  **Window** controls whether the app opens in the tray or hides there
  when you close it. If your desktop has no tray, choose **Show mixer**
  and **Quit app**.

## If you cannot hear anything

- Play audio and follow **Flow** from the app or microphone to its
  channel, then to the mix and output. Check the channel meter, the send
  level and mute in the mix, and the output selected under **Monitor**.
- If OpenXLR cannot connect to the native user service, check its status.

  ```sh
  systemctl --user status openxlr-daemon
  ```

  For a Flatpak or source build, use the launch instructions for that
  installation. See [troubleshooting](manual.md#troubleshooting) for
  missing devices and audio service problems.
- Ask the [OpenXLR Discord community](https://discord.gg/4bswtnGPW4) for
  help. **Options → Support → Collect diagnostics** saves a diagnostic
  archive. Review it before attaching it to an issue.
