Interview Assist for Linux
==========================

Live captions of the interviewer's voice (Deepgram) and, when you press Enter, an answer streamed from OpenAI
that uses your resume and the job description.


Requirements
------------
- 64-bit x86 Linux (x86_64) with glibc: Ubuntu 20.04+, Debian 11+, Fedora 36+ or similar.
  Nothing else to install for .NET: the program is self-contained.
- An X11 session, or a Wayland session with XWayland (the default on GNOME and KDE Plasma).
- The PulseAudio client library, used for audio capture. It works with PulseAudio and with PipeWire
  (through pipewire-pulse, the default on current distributions):
      Debian/Ubuntu:  sudo apt install libpulse0
      Fedora:         sudo dnf install pulseaudio-libs
  "pactl" (pulseaudio-utils) is optional; without it the device lists only show the defaults.
- A Deepgram API key (captions) and an OpenAI API key (answers). Add them in the Settings tab.


Run
---
    tar -xzf InterviewAssist-1.0-linux-x64.tar.gz
    cd InterviewAssist-1.0-linux-x64
    ./interview-assist

Optional: add it to your application menu (per user):

    mkdir -p ~/.local/bin ~/.local/share/applications ~/.local/share/icons/hicolor/256x256/apps
    install -m 755 interview-assist ~/.local/bin/
    install -m 644 interview-assist.png ~/.local/share/icons/hicolor/256x256/apps/
    sed "s|^Exec=.*|Exec=$HOME/.local/bin/interview-assist|" interview-assist.desktop \
        > ~/.local/share/applications/interview-assist.desktop

Errors are written to stderr; start it from a terminal to see them.


Listening to the interviewer
----------------------------
Settings > Transcription > "Listen to":
- System audio (interviewer): what this computer plays (Zoom, Teams, Meet in the browser...). This records the
  monitor of the chosen output device, so it works with headphones.
- Microphone: the chosen input device.


Where settings and keys are stored
----------------------------------
    $XDG_CONFIG_HOME/interview-assist/   (usually ~/.config/interview-assist/)
        settings.json   everything except the API keys
        secrets.json    the API keys, readable only by you (file mode 0600, folder 0700)

The API keys are not encrypted: anyone who can read files as your user can read them. Delete secrets.json (or
clear the keys in Settings) to remove them.


Keyboard
--------
- Enter in the window answers the question in the transcript box; Shift+Enter adds a new line; Esc stops an answer.
- Ctrl + mouse wheel changes the text size.
- Global hotkeys (Settings > Window & appearance) work on X11 sessions. On Wayland, global hotkeys only fire while
  an X11/XWayland window has focus; Enter in this window always works.


Limitations on Linux
--------------------
- No stealth mode: Linux has no way for an app to hide its window from screen sharing or recordings, so the
  setting is not shown. Share a single window or browser tab rather than your whole screen.
- Window opacity needs a compositing window manager (normal on GNOME, KDE Plasma, Cinnamon and Xfce with
  compositing on).
- Always on top is a request to the window manager; a few tiling window managers ignore it.
