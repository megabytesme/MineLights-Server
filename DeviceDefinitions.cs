using System.Linq;
using RGB.NET.Core;
using RGB.NET.Devices.Razer;

/// <summary>Device definitions not yet included in the pinned RGB.NET revision.</summary>
public static class DeviceDefinitions
{
    public static void Register()
    {
        const int krakenKittyV2Pro = 0x0554;
        if (!RazerDeviceProvider.DeviceDefinitions.Any(device => device.ProductId == krakenKittyV2Pro))
        {
            RazerDeviceProvider.DeviceDefinitions.Add(
                krakenKittyV2Pro,
                RGBDeviceType.Headset,
                "Kraken Kitty V2 Pro",
                RGB.NET.Devices.Razer.LedMappings.Headset,
                RazerEndpointType.Headset);
        }
    }
}
