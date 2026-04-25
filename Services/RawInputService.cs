using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KeyPulse.Helpers;
using KeyPulse.Models;

namespace KeyPulse.Services;

/// <summary>
/// Tracks per-device keyboard and mouse activity using Windows Raw Input (WM_INPUT).
/// Works in background/tray mode via RIDEV_INPUTSINK.
/// Keystrokes and mouse clicks are counted per minute; mouse movement is binary per minute.
/// Completed minute buckets are flushed to the database every minute and on shutdown.
/// </summary>
public class RawInputService : IDisposable
{
    /// <summary>
    /// Raised when hold state changes for a device.
    /// True while any key or mouse button is held, false when all are released.
    /// </summary>
    public event Action<string, bool>? ActivityStateChanged;

    #region Win32 constants

    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIDI_DEVICENAME = 0x20000007;

    // Mouse button-down flags (bit 0 of the pair = button down, bit 1 = button up)
    private const ushort RI_MOUSE_BUTTON_1_DOWN = 0x0001;
    private const ushort RI_MOUSE_BUTTON_1_UP = 0x0002;
    private const ushort RI_MOUSE_BUTTON_2_DOWN = 0x0004;
    private const ushort RI_MOUSE_BUTTON_2_UP = 0x0008;
    private const ushort RI_MOUSE_BUTTON_3_DOWN = 0x0010;
    private const ushort RI_MOUSE_BUTTON_3_UP = 0x0020;
    private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
    private const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
    private const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
    private const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;

    private const ushort MOUSE_BUTTON_DOWN_MASK =
        RI_MOUSE_BUTTON_1_DOWN
        | RI_MOUSE_BUTTON_2_DOWN
        | RI_MOUSE_BUTTON_3_DOWN
        | RI_MOUSE_BUTTON_4_DOWN
        | RI_MOUSE_BUTTON_5_DOWN;

    private const ushort MOUSE_BUTTON_UP_MASK =
        RI_MOUSE_BUTTON_1_UP
        | RI_MOUSE_BUTTON_2_UP
        | RI_MOUSE_BUTTON_3_UP
        | RI_MOUSE_BUTTON_4_UP
        | RI_MOUSE_BUTTON_5_UP;

    // RAWKEYBOARD.Flags: bit 0 set = key-up (break), bit 0 clear = key-down (make)
    private const ushort RI_KEY_BREAK = 0x01;

    #endregion

    #region Win32 structs

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>
    /// RAWMOUSE — uses explicit layout to express the usButtonFlags/usButtonData union correctly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)]
        public ushort usFlags;

        // 2 bytes of implicit MSVC padding here to align ULONG to a 4-byte boundary.

        [FieldOffset(4)]
        public uint ulButtons; // union: full 32-bit button state

        [FieldOffset(4)]
        public ushort usButtonFlags; // overlaps ulButtons low word

        [FieldOffset(6)]
        public ushort usButtonData; // overlaps ulButtons high word

        [FieldOffset(8)]
        public uint ulRawButtons;

        [FieldOffset(12)]
        public int lLastX;

        [FieldOffset(16)]
        public int lLastY;

        [FieldOffset(20)]
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    #endregion

    #region Win32 P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader
    );

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    #endregion

    #region Activity bucket

    /// <summary>
    /// Mutable per-(device, minute) activity counters.
    /// All access must be guarded by <see cref="_lock"/>.
    /// </summary>
    private sealed class ActivityBucket
    {
        public int Keystrokes { get; set; }
        public int MouseClicks { get; set; }

        /// <summary>
        /// Set of seconds-of-minute (0–59) in which mouse movement was detected.
        /// Count / 60.0 gives the active fraction.
        /// </summary>
        public HashSet<int> ActiveMovementSeconds { get; } = new();
    }

    #endregion

    // Shared state — all access guarded by _lock
    private readonly object _lock = new();
    private readonly Dictionary<(string DeviceId, DateTime Minute), ActivityBucket> _buckets = new();
    private readonly Dictionary<string, HashSet<ushort>> _pressedKeysByDevice = new();
    private readonly Dictionary<string, HashSet<int>> _pressedMouseButtonsByDevice = new();

    // hDevice handle → DeviceId cache; only touched on the UI thread (WndProc), no lock needed.
    private readonly Dictionary<IntPtr, string?> _deviceHandleCache = new();

    private HwndSource? _hwndSource;
    private readonly DataService _dataService;
    private readonly Timer _flushTimer;
    private bool _disposed;

    public RawInputService(DataService dataService)
    {
        _dataService = dataService;

        // Flush completed minute buckets every 60 seconds on a background thread.
        _flushTimer = new Timer(_ => FlushCompletedMinutes(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Creates the hidden message-only window and registers Raw Input devices.
    /// Must be called on the WPF UI dispatcher thread after the application has started.
    /// </summary>
    public void Start()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // HWND_MESSAGE (-3) parent → invisible, message-only window; no taskbar entry.
            var parameters = new HwndSourceParameters("KeyPulse_RawInput")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3),
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);

            RegisterDevices(_hwndSource.Handle);
        });
    }

    private static void RegisterDevices(IntPtr hwnd)
    {
        var devices = new RAWINPUTDEVICE[]
        {
            // Generic Desktop / Keyboard  (UsagePage 0x01, Usage 0x06)
            new()
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd,
            },
            // Generic Desktop / Mouse     (UsagePage 0x01, Usage 0x02)
            new()
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd,
            },
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Debug.WriteLine($"RawInputService: RegisterRawInputDevices failed (error {Marshal.GetLastWin32Error()})");
        else
            Debug.WriteLine("RawInputService: Raw Input registered successfully.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT)
            return IntPtr.Zero;

        try
        {
            ProcessRawInput(lParam);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RawInputService WndProc error: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr lParam)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;

        // First call: query required buffer size.
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) == uint.MaxValue)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            var bodyPtr = IntPtr.Add(buffer, (int)headerSize);

            // Map raw device handle → our DeviceId string.
            var deviceId = GetDeviceId(header.hDevice);
            if (deviceId == null)
                return;

            var minute = DateTime.Now.TruncateToMinute();

            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(bodyPtr);
                var isKeyDown = (kb.Flags & RI_KEY_BREAK) == 0;
                bool nextActivityState;

                lock (_lock)
                {
                    if (isKeyDown)
                    {
                        GetOrCreateBucket(deviceId, minute).Keystrokes++;
                        var pressedKeys = GetOrCreatePressedKeys(deviceId);
                        pressedKeys.Add(kb.VKey);
                    }
                    else if (_pressedKeysByDevice.TryGetValue(deviceId, out var pressedKeys))
                    {
                        pressedKeys.Remove(kb.VKey);
                    }

                    nextActivityState = ComputeHoldState(deviceId);
                }

                ActivityStateChanged?.Invoke(deviceId, nextActivityState);
            }
            else if (header.dwType == RIM_TYPEMOUSE)
            {
                var mouse = Marshal.PtrToStructure<RAWMOUSE>(bodyPtr);
                bool? nextActivityState = null;

                if (mouse.usButtonFlags == 0)
                {
                    // Pure movement — record which second-of-minute this occurred in.
                    // HashSet.Add is a no-op if this second was already recorded.
                    var second = DateTime.Now.Second; // 0–59
                    lock (_lock)
                    {
                        GetOrCreateBucket(deviceId, minute).ActiveMovementSeconds.Add(second);
                    }
                }

                if ((mouse.usButtonFlags & MOUSE_BUTTON_DOWN_MASK) != 0)
                    lock (_lock)
                    {
                        GetOrCreateBucket(deviceId, minute).MouseClicks++;
                        var pressedButtons = GetOrCreatePressedMouseButtons(deviceId);
                        AddPressedMouseButtons(pressedButtons, mouse.usButtonFlags);
                        nextActivityState = ComputeHoldState(deviceId);
                    }

                if ((mouse.usButtonFlags & MOUSE_BUTTON_UP_MASK) != 0)
                    lock (_lock)
                    {
                        if (_pressedMouseButtonsByDevice.TryGetValue(deviceId, out var pressedButtons))
                            RemovePressedMouseButtons(pressedButtons, mouse.usButtonFlags);
                        nextActivityState = ComputeHoldState(deviceId);
                    }

                if (nextActivityState.HasValue)
                    ActivityStateChanged?.Invoke(deviceId, nextActivityState.Value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void ClearDeviceHoldState(string deviceId)
    {
        lock (_lock)
        {
            _pressedKeysByDevice.Remove(deviceId);
            _pressedMouseButtonsByDevice.Remove(deviceId);
        }

        ActivityStateChanged?.Invoke(deviceId, false);
    }

    private HashSet<ushort> GetOrCreatePressedKeys(string deviceId)
    {
        if (!_pressedKeysByDevice.TryGetValue(deviceId, out var pressedKeys))
        {
            pressedKeys = new HashSet<ushort>();
            _pressedKeysByDevice[deviceId] = pressedKeys;
        }

        return pressedKeys;
    }

    private HashSet<int> GetOrCreatePressedMouseButtons(string deviceId)
    {
        if (!_pressedMouseButtonsByDevice.TryGetValue(deviceId, out var pressedButtons))
        {
            pressedButtons = new HashSet<int>();
            _pressedMouseButtonsByDevice[deviceId] = pressedButtons;
        }

        return pressedButtons;
    }

    private static void AddPressedMouseButtons(HashSet<int> pressedButtons, ushort flags)
    {
        if ((flags & RI_MOUSE_BUTTON_1_DOWN) != 0)
            pressedButtons.Add(1);
        if ((flags & RI_MOUSE_BUTTON_2_DOWN) != 0)
            pressedButtons.Add(2);
        if ((flags & RI_MOUSE_BUTTON_3_DOWN) != 0)
            pressedButtons.Add(3);
        if ((flags & RI_MOUSE_BUTTON_4_DOWN) != 0)
            pressedButtons.Add(4);
        if ((flags & RI_MOUSE_BUTTON_5_DOWN) != 0)
            pressedButtons.Add(5);
    }

    private static void RemovePressedMouseButtons(HashSet<int> pressedButtons, ushort flags)
    {
        if ((flags & RI_MOUSE_BUTTON_1_UP) != 0)
            pressedButtons.Remove(1);
        if ((flags & RI_MOUSE_BUTTON_2_UP) != 0)
            pressedButtons.Remove(2);
        if ((flags & RI_MOUSE_BUTTON_3_UP) != 0)
            pressedButtons.Remove(3);
        if ((flags & RI_MOUSE_BUTTON_4_UP) != 0)
            pressedButtons.Remove(4);
        if ((flags & RI_MOUSE_BUTTON_5_UP) != 0)
            pressedButtons.Remove(5);
    }

    private bool ComputeHoldState(string deviceId)
    {
        var hasPressedKeys = _pressedKeysByDevice.TryGetValue(deviceId, out var pressedKeys) && pressedKeys.Count > 0;
        var hasPressedMouseButtons =
            _pressedMouseButtonsByDevice.TryGetValue(deviceId, out var pressedButtons) && pressedButtons.Count > 0;
        return hasPressedKeys || hasPressedMouseButtons;
    }

    /// <summary>
    /// Returns the existing bucket for (deviceId, minute) or creates an empty one.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private ActivityBucket GetOrCreateBucket(string deviceId, DateTime minute)
    {
        var key = (deviceId, minute);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new ActivityBucket();
            _buckets[key] = bucket;
        }

        return bucket;
    }

    /// <summary>
    /// Resolves a Raw Input device handle to a KeyPulse DeviceId string (USB\VID_xxxx&PID_xxxx).
    /// Result is cached for the lifetime of the handle (i.e., while the device is connected).
    /// Called only on the UI thread (inside WndProc), so no lock is needed for the cache.
    /// </summary>
    private string? GetDeviceId(IntPtr hDevice)
    {
        if (_deviceHandleCache.TryGetValue(hDevice, out var cached))
            return cached;

        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);

        if (size == 0)
        {
            _deviceHandleCache[hDevice] = null;
            return null;
        }

        // RIDI_DEVICENAME returns size in characters (Unicode: 2 bytes each).
        var namePtr = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, namePtr, ref size);
            var devicePath = Marshal.PtrToStringUni(namePtr) ?? "";
            var deviceId = ParseDeviceId(devicePath);
            _deviceHandleCache[hDevice] = deviceId;
            Debug.WriteLine($"RawInputService: mapped hDevice 0x{hDevice:X} → '{deviceId}' (path: {devicePath})");
            return deviceId;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    /// <summary>
    /// Parses a Raw Input device path such as
    ///   \\?\HID#VID_046D&amp;PID_C548&amp;MI_00#7&amp;...
    /// into the KeyPulse format USB\VID_046D&amp;PID_C548.
    /// </summary>
    private static string? ParseDeviceId(string devicePath)
    {
        var vidIdx = devicePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0)
            return null;

        var vidStart = vidIdx + 4;
        var vidEnd = devicePath.IndexOfAny(['&', '#', '\\'], vidStart);
        if (vidEnd < 0)
            vidEnd = devicePath.Length;
        var vid = devicePath[vidStart..vidEnd];

        var pidIdx = devicePath.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pidIdx < 0)
            return null;

        var pidStart = pidIdx + 4;
        var pidEnd = devicePath.IndexOfAny(['&', '#', '\\'], pidStart);
        if (pidEnd < 0)
            pidEnd = devicePath.Length;
        var pid = devicePath[pidStart..pidEnd];

        if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid))
            return null;

        return $"USB\\VID_{vid.ToUpperInvariant()}&PID_{pid.ToUpperInvariant()}";
    }

    /// <summary>
    /// Flushes all minute buckets whose minute is strictly before the current minute
    /// (i.e., they are definitely complete). Called by the background timer.
    /// </summary>
    private void FlushCompletedMinutes()
    {
        var currentMinute = DateTime.Now.TruncateToMinute();
        FlushMinutes(key => key.Minute < currentMinute);
    }

    /// <summary>
    /// Flushes all buckets unconditionally (used on shutdown to capture the current partial minute).
    /// </summary>
    private void FlushAllMinutes()
    {
        FlushMinutes(_ => true);
    }

    private void FlushMinutes(Func<(string DeviceId, DateTime Minute), bool> predicate)
    {
        List<ActivitySnapshot> snapshots;

        lock (_lock)
        {
            var candidates = _buckets.Where(kvp => predicate(kvp.Key)).ToList();
            if (candidates.Count == 0)
                return;

            snapshots = candidates
                .Select(kvp => new ActivitySnapshot
                {
                    DeviceId = kvp.Key.DeviceId,
                    Minute = kvp.Key.Minute,
                    Keystrokes = kvp.Value.Keystrokes,
                    MouseClicks = kvp.Value.MouseClicks,
                    MouseActiveSeconds = (byte)kvp.Value.ActiveMovementSeconds.Count,
                })
                .ToList();

            foreach (var (key, _) in candidates)
                _buckets.Remove(key);
        }

        _dataService.SaveActivitySnapshots(snapshots);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _flushTimer.Dispose();

        // Flush any remaining data (including the current partial minute).
        try
        {
            FlushAllMinutes();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RawInputService flush on dispose failed: {ex.Message}");
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.Dispose();
                _hwndSource = null;
            }
        });

        GC.SuppressFinalize(this);
    }
}
