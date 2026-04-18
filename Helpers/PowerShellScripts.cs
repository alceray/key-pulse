using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;

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

        return null;
    }

    private static Collection<PSObject> RunPowerShellScript(string script)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(script);
        var results = ps.Invoke();

        if (ps.Streams.Error.Count > 0)
            foreach (var error in ps.Streams.Error)
                Debug.WriteLine($"PowerShell Error: {error}");

        return results;
    }
}
