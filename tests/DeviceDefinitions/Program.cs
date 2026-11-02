using RGB.NET.Core;
using RGB.NET.Devices.Logitech;
using RGB.NET.Devices.Razer;

DeviceDefinitions.Register();
DeviceDefinitions.Register(); // Recreating the server must not register the headset twice.

var headset = RazerDeviceProvider.DeviceDefinitions.Single(device => device.ProductId == 0x0554);
if (headset.DeviceType != RGBDeviceType.Headset || headset.CustomData != RazerEndpointType.Headset || headset.Name != "Kraken Kitty V2 Pro")
    throw new InvalidOperationException("Kraken Kitty V2 Pro definition is incorrect.");

var keyboard = LogitechDeviceProvider.PerKeyDeviceDefinitions.Single(device => device.ProductId == 0xC363);
if (keyboard.DeviceType != RGBDeviceType.Keyboard || !keyboard.LedMapping.Any())
    throw new InvalidOperationException("G515 Rapid TKL per-key definition is missing.");

Console.WriteLine("Kraken Kitty V2 Pro and Logitech G515 Rapid TKL definitions verified.");
