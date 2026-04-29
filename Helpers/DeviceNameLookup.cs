using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace KeyPulse.Helpers;

public static class DeviceNameLookup
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int CR_SUCCESS = 0x00000000;
    private const int CR_BUFFER_SMALL = 0x0000001A;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new()
    {
        fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
        pid = 4,
    };

    public static string? GetDeviceName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        try
        {
            var normalizedDeviceId = deviceId.Trim();

            var setupApiName = TryGetDeviceNameFromSetupApi(normalizedDeviceId);
            if (!string.IsNullOrWhiteSpace(setupApiName))
            {
                Log.Debug(
                    "DeviceNameLookup resolved via SetupAPI: {DeviceName} for DeviceId={DeviceId}",
                    setupApiName,
                    deviceId
                );
                return setupApiName;
            }

            var powerShellName = PowershellScripts.GetDeviceName(normalizedDeviceId);
            if (!string.IsNullOrWhiteSpace(powerShellName))
            {
                Log.Debug(
                    "DeviceNameLookup resolved via PowerShell fallback: {DeviceName} for DeviceId={DeviceId}",
                    powerShellName,
                    deviceId
                );
                return powerShellName;
            }

            Log.Debug(
                "DeviceNameLookup returned no result from SetupAPI or PowerShell for DeviceId={DeviceId}",
                deviceId
            );
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DeviceNameLookup failed for DeviceId={DeviceId}", deviceId);
            return null;
        }
    }

    private static string? TryGetDeviceNameFromSetupApi(string deviceId)
    {
        var deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (deviceInfoSet == InvalidHandleValue)
            return null;

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
                    break;

                var instanceId = GetDeviceInstanceId(deviceInfoSet, ref deviceInfoData);
                if (string.IsNullOrWhiteSpace(instanceId) || !IsDeviceIdMatch(instanceId, deviceId))
                    continue;

                var busReported = GetDevicePropertyString(
                    deviceInfoSet,
                    ref deviceInfoData,
                    DEVPKEY_Device_BusReportedDeviceDesc
                );
                if (!string.IsNullOrWhiteSpace(busReported))
                    return busReported;

                // Fallback path through cfgmgr32 for systems where SetupDiGetDeviceProperty
                // does not expose this property reliably.
                var busReportedFromCfgMgr = GetDevNodePropertyString(
                    deviceInfoData.DevInst,
                    DEVPKEY_Device_BusReportedDeviceDesc
                );
                if (!string.IsNullOrWhiteSpace(busReportedFromCfgMgr))
                    return busReportedFromCfgMgr;
            }

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData)
    {
        var builder = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfoData, builder, builder.Capacity, out _)
            ? builder.ToString()
            : null;
    }

    private static string? GetDevicePropertyString(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        DEVPROPKEY key
    )
    {
        uint propertyType;
        uint requiredSize;

        var firstPass = SetupDiGetDeviceProperty(
            deviceInfoSet,
            ref deviceInfoData,
            ref key,
            out propertyType,
            null,
            0,
            out requiredSize,
            0
        );

        if (firstPass)
            return null;

        var error = Marshal.GetLastWin32Error();
        if (error != ERROR_INSUFFICIENT_BUFFER || requiredSize == 0)
            return null;

        var buffer = new byte[requiredSize];
        var secondPass = SetupDiGetDeviceProperty(
            deviceInfoSet,
            ref deviceInfoData,
            ref key,
            out propertyType,
            buffer,
            (uint)buffer.Length,
            out requiredSize,
            0
        );

        if (!secondPass)
            return null;

        var value = DecodeUnicodeProperty(buffer);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetDevNodePropertyString(uint devInst, DEVPROPKEY key)
    {
        uint propertyType;
        uint requiredSize = 0;

        var firstPass = CM_Get_DevNode_Property(devInst, ref key, out propertyType, null, ref requiredSize, 0);
        if (firstPass != CR_BUFFER_SMALL || requiredSize == 0)
            return null;

        var buffer = new byte[requiredSize];
        var secondPass = CM_Get_DevNode_Property(devInst, ref key, out propertyType, buffer, ref requiredSize, 0);
        if (secondPass != CR_SUCCESS)
            return null;

        var value = DecodeUnicodeProperty(buffer);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string DecodeUnicodeProperty(byte[] buffer)
    {
        var raw = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // Handle REG_MULTI_SZ or string-list payloads by taking first non-empty segment.
        var first = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return (first ?? raw).Trim();
    }

    private static bool IsDeviceIdMatch(string instanceId, string deviceId)
    {
        if (string.Equals(instanceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        return instanceId.Contains(deviceId, StringComparison.OrdinalIgnoreCase)
            || deviceId.Contains(instanceId, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData
    );

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize
    );

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern int CM_Get_DevNode_Property(
        uint dnDevInst,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint flags
    );
}
