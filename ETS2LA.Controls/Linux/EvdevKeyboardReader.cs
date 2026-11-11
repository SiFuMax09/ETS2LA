using System.Globalization;
using ETS2LA.Logging;
using static ETS2LA.Translations.T;

namespace ETS2LA.Controls.Linux;

/// <summary>
///  Reads keyboard events straight from the kernel (evdev, /dev/input/event*).
///  Global input hooks (SharpHook / X11) can't see keys while a native Wayland
///  window has focus, this reader doesn't care about focus at all.
///  It needs read access to /dev/input, which on most distros means the user
///  has to be in the "input" group.
///
///  Key names match what SharpHook reports (KeyCode without the "Vc" prefix),
///  so existing keybinds like "RightAlt" keep working.
/// </summary>
internal sealed class EvdevKeyboardReader : IDisposable
{
    private const ushort EV_KEY = 1;
    private const string InputDirectory = "/dev/input";
    private const string SysClassInput = "/sys/class/input";
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    // struct input_event { struct timeval time; __u16 type; __u16 code; __s32 value; }
    private static readonly int TimevalSize = IntPtr.Size * 2;
    private static readonly int EventSize = TimevalSize + 8;

    private readonly Action<bool, string> _onKey;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _openDevices = new();
    // How many devices currently hold a key down. Only the first press and the
    // last release are reported, so duplicate devices can't double-trigger a key.
    private readonly Dictionary<string, int> _pressCounts = new();
    private bool _permissionDeniedLogged;
    private Thread? _rescanThread;

    public EvdevKeyboardReader(Action<bool, string> onKey)
    {
        _onKey = onKey;
    }

    /// <summary>
    ///  True when running inside a Wayland session, where X11 hooks are unreliable.
    /// </summary>
    public static bool IsWaylandSession()
    {
        return string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    /// <summary>
    ///  Opens all readable keyboards. Returns false if none could be opened.
    /// </summary>
    public bool Start()
    {
        ScanDevices();

        lock (_lock)
        {
            if (_openDevices.Count == 0)
                return false;
        }

        _rescanThread = new Thread(RescanLoop) { IsBackground = true, Name = "ETS2LA evdev rescan" };
        _rescanThread.Start();
        return true;
    }

    private void RescanLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Task.Delay(RescanInterval, _cts.Token).Wait(); }
            catch { return; }

            try { ScanDevices(); }
            catch (Exception ex)
            {
                Logger.Warn(_("Failed to scan input devices: {0}", ex.Message));
            }
        }
    }

    private void ScanDevices()
    {
        if (!Directory.Exists(InputDirectory))
            return;

        foreach (var path in Directory.GetFiles(InputDirectory, "event*"))
        {
            lock (_lock)
            {
                if (_openDevices.Contains(path))
                    continue;
            }

            var name = Path.GetFileName(path);
            if (!IsKeyboard(name))
                continue;

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, false);
            }
            catch (UnauthorizedAccessException)
            {
                if (!_permissionDeniedLogged)
                {
                    _permissionDeniedLogged = true;
                    Logger.Warn(_("No permission to read keyboards from {0}. Add your user to the 'input' group to enable keyboard controls on Wayland.", InputDirectory));
                }
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            lock (_lock)
            {
                _openDevices.Add(path);
            }

            var thread = new Thread(() => ReadLoop(stream, path)) { IsBackground = true, Name = $"ETS2LA evdev {name}" };
            thread.Start();
        }
    }

    /// <summary>
    ///  A device counts as a keyboard if it reports ESC, ENTER and A.
    ///  This skips mice, power buttons, and other devices that only have a few keys.
    /// </summary>
    private static bool IsKeyboard(string eventName)
    {
        try
        {
            var capabilitiesPath = Path.Combine(SysClassInput, eventName, "device", "capabilities", "key");
            if (!File.Exists(capabilitiesPath))
                return false;

            // Space separated hex words, most significant word first.
            // Each word is as wide as the kernel's "long".
            var words = File.ReadAllText(capabilitiesPath)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Reverse()
                .Select(w => ulong.Parse(w, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();

            var bitsPerWord = IntPtr.Size * 8;
            bool Has(int code)
            {
                var index = code / bitsPerWord;
                return index < words.Length && (words[index] & (1UL << (code % bitsPerWord))) != 0;
            }

            return Has(1) && Has(28) && Has(30); // KEY_ESC, KEY_ENTER, KEY_A
        }
        catch
        {
            return false;
        }
    }

    private void ReadLoop(FileStream stream, string path)
    {
        var buffer = new byte[EventSize];
        var held = new HashSet<string>();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = 0;
                while (read < EventSize)
                {
                    var n = stream.Read(buffer, read, EventSize - read);
                    if (n <= 0)
                        return;
                    read += n;
                }

                var type = BitConverter.ToUInt16(buffer, TimevalSize);
                if (type != EV_KEY)
                    continue;

                var code = BitConverter.ToUInt16(buffer, TimevalSize + 2);
                var value = BitConverter.ToInt32(buffer, TimevalSize + 4);
                if (value == 2) // auto repeat
                    continue;

                if (!KeyNames.TryGetValue(code, out var keyName))
                    continue;

                var pressed = value != 0;
                if (pressed ? !held.Add(keyName) : !held.Remove(keyName))
                    continue;

                Report(keyName, pressed);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            // Device unplugged or we are shutting down. Release anything still held,
            // otherwise a keyboard that disappears could leave a key stuck.
            foreach (var keyName in held)
                Report(keyName, false);

            stream.Dispose();
            lock (_lock)
            {
                _openDevices.Remove(path);
            }
        }
    }

    private void Report(string keyName, bool pressed)
    {
        lock (_lock)
        {
            _pressCounts.TryGetValue(keyName, out var count);
            if (pressed)
            {
                _pressCounts[keyName] = count + 1;
                if (count != 0)
                    return;
            }
            else
            {
                if (count <= 0)
                    return;
                if (count == 1)
                    _pressCounts.Remove(keyName);
                else
                {
                    _pressCounts[keyName] = count - 1;
                    return;
                }
            }
        }

        _onKey(pressed, keyName);
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    /// <summary>
    ///  evdev key code (linux/input-event-codes.h) to SharpHook key name (without "Vc").
    /// </summary>
    private static readonly Dictionary<int, string> KeyNames = new()
    {
        [1] = "Escape",
        [2] = "1", [3] = "2", [4] = "3", [5] = "4", [6] = "5",
        [7] = "6", [8] = "7", [9] = "8", [10] = "9", [11] = "0",
        [12] = "Minus", [13] = "Equals", [14] = "Backspace", [15] = "Tab",
        [16] = "Q", [17] = "W", [18] = "E", [19] = "R", [20] = "T",
        [21] = "Y", [22] = "U", [23] = "I", [24] = "O", [25] = "P",
        [26] = "OpenBracket", [27] = "CloseBracket", [28] = "Enter",
        [29] = "LeftControl",
        [30] = "A", [31] = "S", [32] = "D", [33] = "F", [34] = "G",
        [35] = "H", [36] = "J", [37] = "K", [38] = "L",
        [39] = "Semicolon", [40] = "Quote", [41] = "BackQuote",
        [42] = "LeftShift", [43] = "Backslash",
        [44] = "Z", [45] = "X", [46] = "C", [47] = "V", [48] = "B",
        [49] = "N", [50] = "M",
        [51] = "Comma", [52] = "Period", [53] = "Slash",
        [54] = "RightShift", [55] = "NumPadMultiply", [56] = "LeftAlt",
        [57] = "Space", [58] = "CapsLock",
        [59] = "F1", [60] = "F2", [61] = "F3", [62] = "F4", [63] = "F5",
        [64] = "F6", [65] = "F7", [66] = "F8", [67] = "F9", [68] = "F10",
        [69] = "NumLock", [70] = "ScrollLock",
        [71] = "NumPad7", [72] = "NumPad8", [73] = "NumPad9", [74] = "NumPadSubtract",
        [75] = "NumPad4", [76] = "NumPad5", [77] = "NumPad6", [78] = "NumPadAdd",
        [79] = "NumPad1", [80] = "NumPad2", [81] = "NumPad3",
        [82] = "NumPad0", [83] = "NumPadDecimal",
        [86] = "102", [87] = "F11", [88] = "F12",
        [96] = "NumPadEnter", [97] = "RightControl", [98] = "NumPadDivide",
        [99] = "PrintScreen", [100] = "RightAlt",
        [102] = "Home", [103] = "Up", [104] = "PageUp", [105] = "Left",
        [106] = "Right", [107] = "End", [108] = "Down", [109] = "PageDown",
        [110] = "Insert", [111] = "Delete",
        [113] = "VolumeMute", [114] = "VolumeDown", [115] = "VolumeUp",
        [117] = "NumPadEquals", [119] = "Pause",
        [125] = "LeftMeta", [126] = "RightMeta", [127] = "ContextMenu",
        [163] = "MediaNext", [164] = "MediaPlay", [165] = "MediaPrevious", [166] = "MediaStop",
        [183] = "F13", [184] = "F14", [185] = "F15", [186] = "F16",
        [187] = "F17", [188] = "F18", [189] = "F19", [190] = "F20",
        [191] = "F21", [192] = "F22", [193] = "F23", [194] = "F24",
    };
}
