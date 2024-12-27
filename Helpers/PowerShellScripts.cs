using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace KeyPulse.Helpers
{
    public static class PowershellScripts
    {
        public static string? GetDeviceName(string deviceId)
        {
            string escapedDeviceId = deviceId.Replace(@"\", @"\\");
            string script = $$"""
                Get-PnpDevice -PresentOnly | Where-Object {
                    $_.InstanceId -match '{{escapedDeviceId}}'
                } | ForEach-Object {
                    $properties = Get-PnpDeviceProperty -InstanceId $_.InstanceId
                    ($properties | Where-Object { $_.KeyName -eq 'DEVPKEY_Device_BusReportedDeviceDesc' }).Data
                }
                """;

            var results = RunPowerShellScript(script);
            foreach (var result in results)
            {
                if (result?.BaseObject is string deviceName && !string.IsNullOrEmpty(deviceName))
                {
                    return deviceName;
                }
            }

            return null;
        }

        private static Collection<PSObject> RunPowerShellScript(string script)
        {
            using PowerShell ps = PowerShell.Create();
            ps.AddScript(script);
            var results = ps.Invoke();
            
            if (ps.Streams.Error.Count > 0)
            {
                foreach (var error in ps.Streams.Error)
                {
                    Debug.WriteLine($"PowerShell Error: {error}");
                }
            }

            return results;
        }
    }
}
