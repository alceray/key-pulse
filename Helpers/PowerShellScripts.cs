using System.Collections.ObjectModel;
using System.Management.Automation;
using Serilog;

namespace KeyPulse.Helpers;

public static class PowershellScripts
{
    public static string? GetDeviceName(string deviceId)
    {
        var escapedDeviceId = deviceId.Replace(@"\", @"\\");
        var script = $$"""
            Get-PnpDevice -PresentOnly | Where-Object {
                $_.InstanceId -match '{{escapedDeviceId}}'
            } | ForEach-Object {
                $properties = Get-PnpDeviceProperty -InstanceId $_.InstanceId
                ($properties | Where-Object { $_.KeyName -eq 'DEVPKEY_Device_BusReportedDeviceDesc' }).Data
            }
            """;

        var results = RunPowerShellScript(script);
        foreach (var result in results)
            if (result?.BaseObject is string deviceName && !string.IsNullOrEmpty(deviceName))
                return deviceName;

        Log.Debug("PowerShell device-name lookup returned no result for DeviceId={DeviceId}", deviceId);
        return null;
    }

    private static Collection<PSObject> RunPowerShellScript(string script)
    {
        using var ps = PowerShell.Create();
        ps.AddScript("Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process");
        ps.AddScript(script);
        var results = ps.Invoke();

        if (ps.Streams.Error.Count > 0)
            foreach (var error in ps.Streams.Error)
                Log.Warning("PowerShell Error: {PowerShellError}", error.ToString());

        return results;
    }
}
