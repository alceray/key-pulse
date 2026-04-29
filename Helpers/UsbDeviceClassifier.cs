using System.Management;
using KeyPulse.Models;

namespace KeyPulse.Helpers;

internal static class UsbDeviceClassifier
{
    internal static DeviceTypes? GetInterfaceSignal(ManagementBaseObject obj)
    {
        var service = obj.GetPropertyValue("Service")?.ToString();
        if (string.Equals(service, "kbdhid", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Keyboard;
        if (string.Equals(service, "mouhid", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Mouse;

        var classGuid = obj.GetPropertyValue("ClassGuid")?.ToString()?.Trim('{', '}');
        if (string.Equals(classGuid, "4d36e96b-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Keyboard;
        if (string.Equals(classGuid, "4d36e96f-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Mouse;

        var pnpClass = obj.GetPropertyValue("PNPClass")?.ToString();
        if (string.Equals(pnpClass, "Keyboard", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Keyboard;
        if (string.Equals(pnpClass, "Mouse", StringComparison.OrdinalIgnoreCase))
            return DeviceTypes.Mouse;

        return null;
    }

    internal static DeviceTypes ResolveDeviceType(int keyboardSignals, int mouseSignals)
    {
        if (keyboardSignals == 1 && mouseSignals == 1)
            return DeviceTypes.Mouse;
        if (keyboardSignals == 2 && mouseSignals == 1)
            return DeviceTypes.Keyboard;

        return DeviceTypes.Other;
    }
}
