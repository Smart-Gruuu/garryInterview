using InterviewAssist.Core.Models;
using InterviewAssist.Desktop.Platform;

namespace InterviewAssist.Presentation.Tests;

/// <summary>The pure helpers of the Linux desktop app: hotkey parsing and pactl output parsing.</summary>
public class DesktopPlatformTests
{
    // ---- HotkeyParser ----

    [Theory]
    [InlineData("Ctrl+Shift+Enter", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x0Du)]
    [InlineData("alt + q", HotkeyModifiers.Alt, (uint)'Q')]
    [InlineData("F9", HotkeyModifiers.None, 0x78u)]
    [InlineData("Control+Windows+f24", HotkeyModifiers.Control | HotkeyModifiers.Win, 0x87u)]
    [InlineData("Ctrl+Alt+7", HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)'7')]
    [InlineData("Shift+PgDn", HotkeyModifiers.Shift, 0x22u)]
    public void Hotkeys_parse_case_insensitively(string text, HotkeyModifiers modifiers, uint virtualKey)
    {
        Assert.True(HotkeyParser.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, virtualKey), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("  none ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+Ä")]
    public void Invalid_or_disabled_hotkeys_do_not_parse(string? text)
    {
        Assert.False(HotkeyParser.TryParse(text, out var hotkey));
        Assert.Equal(default, hotkey);
    }

    [Fact]
    public void Every_hotkey_preset_parses_and_is_safe_as_a_global_hotkey()
    {
        foreach (var preset in SettingOptions.Hotkeys.Where(h => !HotkeyParser.IsNone(h)))
        {
            Assert.True(HotkeyParser.TryParse(preset, out var hotkey), preset);
            Assert.True(HotkeyParser.IsSafeGlobal(hotkey), preset);
        }
    }

    [Theory]
    [InlineData("Enter", false)]
    [InlineData("Shift+A", false)]
    [InlineData("Ctrl+Enter", true)]
    [InlineData("Alt+Space", true)]
    [InlineData("Win+H", true)]
    [InlineData("F8", true)]
    public void Only_combinations_with_ctrl_alt_or_win_and_f_keys_are_safe_globally(string text, bool safe)
    {
        Assert.True(HotkeyParser.TryParse(text, out var hotkey));
        Assert.Equal(safe, HotkeyParser.IsSafeGlobal(hotkey));
    }

    [Fact]
    public void Modifiers_map_to_the_windows_and_x11_masks()
    {
        var all = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win;

        // MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8.
        Assert.Equal(0x2u, HotkeyParser.ToWindowsModifiers(HotkeyModifiers.Control));
        Assert.Equal(0xFu, HotkeyParser.ToWindowsModifiers(all));

        // X.h: ShiftMask 1, ControlMask 4, Mod1Mask (Alt) 8, Mod4Mask (Super) 64.
        Assert.Equal(0u, HotkeyParser.ToX11Modifiers(HotkeyModifiers.None));
        Assert.Equal(4u, HotkeyParser.ToX11Modifiers(HotkeyModifiers.Control));
        Assert.Equal(1u | 4u | 8u | 64u, HotkeyParser.ToX11Modifiers(all));
    }

    [Theory]
    [InlineData("Enter", new[] { "Return", "KP_Enter" })]
    [InlineData("Q", new[] { "q" })]
    [InlineData("7", new[] { "7" })]
    [InlineData("F9", new[] { "F9" })]
    [InlineData("PageUp", new[] { "Prior" })]
    [InlineData("Space", new[] { "space" })]
    [InlineData("Backspace", new[] { "BackSpace" })]
    public void Keys_map_to_x11_keysym_names(string key, string[] keysyms)
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+" + key, out var hotkey));
        Assert.Equal(keysyms, HotkeyParser.GetX11KeysymNames(hotkey.VirtualKey));
    }

    [Fact]
    public void Unknown_virtual_keys_have_no_keysym()
    {
        Assert.Empty(HotkeyParser.GetX11KeysymNames(0xFF));
    }

    // ---- pactl output ----

    private const string ShortSinks =
        "0\talsa_output.pci-0000_00_1f.3.analog-stereo\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tSUSPENDED\n" +
        "57\tbluez_output.AC_80_0A_11_22_33.1\tmodule-bluez5-device.c\ts16le 2ch 48000Hz\tRUNNING\n";

    private const string ShortSources =
        "0\talsa_output.pci-0000_00_1f.3.analog-stereo.monitor\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tSUSPENDED\r\n" +
        "1\talsa_input.pci-0000_00_1f.3.analog-stereo\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tSUSPENDED\r\n" +
        "\r\n";

    private static readonly string LongSources = string.Join(
        "\n",
        "Source #0",
        "\tState: SUSPENDED",
        "\tName: alsa_output.pci-0000_00_1f.3.analog-stereo.monitor",
        "\tDescription: Monitor of Built-in Audio Analog Stereo",
        "\tMonitor of Sink: alsa_output.pci-0000_00_1f.3.analog-stereo",
        "\tProperties:",
        "\t\tdevice.description = \"Monitor of Built-in Audio\"",
        "\t\tName: not-a-field",
        "",
        "Source #1",
        "\tState: RUNNING",
        "\tName: alsa_input.usb-Blue_Yeti-00.analog-stereo",
        "\tDescription: Yeti Stereo Microphone Analog Stereo",
        "\tMonitor of Sink: n/a",
        "\tPorts:",
        "\t\tanalog-input-mic: Microphone (type: Mic, priority: 8700, available)",
        "",
        "Source #2",
        "\tName: virtual_mic",
        "\tMonitor of Sink: n/a",
        "");

    [Fact]
    public void Short_lists_give_names_and_flag_monitor_sources()
    {
        var sinks = PactlOutputParser.ParseShortList(ShortSinks);
        var sources = PactlOutputParser.ParseShortList(ShortSources);

        Assert.Equal(["alsa_output.pci-0000_00_1f.3.analog-stereo", "bluez_output.AC_80_0A_11_22_33.1"], sinks.Select(s => s.Name));
        Assert.All(sinks, s => Assert.False(s.IsMonitor));
        Assert.Equal(
            [
                new PactlEntry("alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", true),
                new PactlEntry("alsa_input.pci-0000_00_1f.3.analog-stereo", "alsa_input.pci-0000_00_1f.3.analog-stereo", false),
            ],
            sources);
    }

    [Fact]
    public void Long_lists_read_the_block_fields_and_ignore_nested_properties()
    {
        var sources = PactlOutputParser.ParseList(LongSources);

        Assert.Equal(
            [
                new PactlEntry(
                    "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", "Monitor of Built-in Audio Analog Stereo", true),
                new PactlEntry("alsa_input.usb-Blue_Yeti-00.analog-stereo", "Yeti Stereo Microphone Analog Stereo", false),
                new PactlEntry("virtual_mic", "virtual_mic", false),
            ],
            sources);
    }

    [Fact]
    public void Unexpected_output_gives_no_entries()
    {
        Assert.Empty(PactlOutputParser.ParseList("Connection failure: Connection refused\n"));
        Assert.Empty(PactlOutputParser.ParseShortList(""));
    }

    [Fact]
    public void Info_fields_are_read_by_name()
    {
        const string info = "Server String: /run/user/1000/pulse/native\n" +
                            "Server Name: PulseAudio (on PipeWire 1.0.5)\n" +
                            "Default Sink: alsa_output.pci-0000_00_1f.3.analog-stereo\n" +
                            "Default Source: \n";

        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", PactlOutputParser.ParseInfoField(info, "Default Sink"));
        Assert.Null(PactlOutputParser.ParseInfoField(info, "Default Source"));
        Assert.Null(PactlOutputParser.ParseInfoField(info, "Cookie"));
    }

    [Fact]
    public void Devices_are_sinks_for_system_audio_and_non_monitor_sources_for_microphones()
    {
        var sinks = PactlOutputParser.ParseShortList(ShortSinks);
        var sources = PactlOutputParser.ParseList(LongSources);

        var devices = PactlOutputParser.BuildDevices(
            sinks, sources, "bluez_output.AC_80_0A_11_22_33.1", "alsa_input.usb-Blue_Yeti-00.analog-stereo");

        Assert.Equal(
            [
                new AudioDeviceInfo("alsa_output.pci-0000_00_1f.3.analog-stereo", "alsa_output.pci-0000_00_1f.3.analog-stereo", AudioSourceKind.SystemAudio, false),
                new AudioDeviceInfo("bluez_output.AC_80_0A_11_22_33.1", "bluez_output.AC_80_0A_11_22_33.1", AudioSourceKind.SystemAudio, true),
                new AudioDeviceInfo("alsa_input.usb-Blue_Yeti-00.analog-stereo", "Yeti Stereo Microphone Analog Stereo", AudioSourceKind.Microphone, true),
                new AudioDeviceInfo("virtual_mic", "virtual_mic", AudioSourceKind.Microphone, false),
            ],
            devices);
    }

    [Fact]
    public void Without_pactl_the_server_defaults_are_offered()
    {
        Assert.Equal(
            [
                new AudioDeviceInfo(PactlOutputParser.DefaultMonitorSource, "Default output (what you hear)", AudioSourceKind.SystemAudio, true),
                new AudioDeviceInfo(PactlOutputParser.DefaultSource, "Default microphone", AudioSourceKind.Microphone, true),
            ],
            PactlOutputParser.FallbackDevices);
    }

    [Theory]
    [InlineData(AudioSourceKind.SystemAudio, null, "@DEFAULT_MONITOR@")]
    [InlineData(AudioSourceKind.SystemAudio, "  ", "@DEFAULT_MONITOR@")]
    [InlineData(AudioSourceKind.SystemAudio, "alsa_output.x", "alsa_output.x.monitor")]
    [InlineData(AudioSourceKind.SystemAudio, "alsa_output.x.monitor", "alsa_output.x.monitor")]
    [InlineData(AudioSourceKind.SystemAudio, "@DEFAULT_MONITOR@", "@DEFAULT_MONITOR@")]
    [InlineData(AudioSourceKind.Microphone, null, null)]
    [InlineData(AudioSourceKind.Microphone, " alsa_input.y ", "alsa_input.y")]
    public void Capture_device_is_the_sink_monitor_or_the_source(AudioSourceKind kind, string? deviceId, string? expected)
    {
        Assert.Equal(expected, PactlOutputParser.ResolveCaptureDevice(kind, deviceId));
    }

    private static readonly string LongSinks = string.Join(
        "\n",
        "Sink #0",
        "\tState: SUSPENDED",
        "\tName: alsa_output.pci-0000_00_1f.3.analog-stereo",
        "\tDescription: Built-in Audio Analog Stereo",
        "\tMonitor Source: alsa_output.pci-0000_00_1f.3.analog-stereo.monitor.2",
        "\tProperties:",
        "\t\tMonitor Source: not-a-field",
        "",
        "Sink #1",
        "\tName: null_sink",
        "\tMonitor Source: n/a",
        "");

    [Fact]
    public void Long_sink_lists_carry_the_monitor_source()
    {
        Assert.Equal(
            [
                new PactlEntry("alsa_output.pci-0000_00_1f.3.analog-stereo", "Built-in Audio Analog Stereo", false,
                    "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor.2"),
                new PactlEntry("null_sink", "null_sink", false),
            ],
            PactlOutputParser.ParseList(LongSinks));
    }

    [Fact]
    public void System_audio_uses_the_listed_monitor_source_when_known()
    {
        var monitors = PactlOutputParser.BuildMonitorSourceMap(PactlOutputParser.ParseList(LongSinks));

        Assert.Equal(
            "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor.2",
            PactlOutputParser.ResolveCaptureDevice(AudioSourceKind.SystemAudio, " alsa_output.pci-0000_00_1f.3.analog-stereo ", monitors));
        Assert.Equal("null_sink.monitor", PactlOutputParser.ResolveCaptureDevice(AudioSourceKind.SystemAudio, "null_sink", monitors));
        Assert.Equal("@DEFAULT_MONITOR@", PactlOutputParser.ResolveCaptureDevice(AudioSourceKind.SystemAudio, null, monitors));
        Assert.Equal("alsa_input.y", PactlOutputParser.ResolveCaptureDevice(AudioSourceKind.Microphone, "alsa_input.y", monitors));
    }

    [Theory]
    [InlineData(AudioSourceKind.SystemAudio, "alsa_output.x", true)]
    [InlineData(AudioSourceKind.SystemAudio, "alsa_output.x.monitor", false)]
    [InlineData(AudioSourceKind.SystemAudio, "@DEFAULT_MONITOR@", false)]
    [InlineData(AudioSourceKind.SystemAudio, " ", false)]
    [InlineData(AudioSourceKind.Microphone, "alsa_input.y", false)]
    public void Only_system_audio_sink_names_need_a_monitor_lookup(AudioSourceKind kind, string deviceId, bool expected)
    {
        Assert.Equal(expected, PactlOutputParser.IsSinkName(kind, deviceId));
    }
}
