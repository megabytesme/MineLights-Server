using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RGB.NET.Core;
using RGB.NET.Devices.Asus;
using RGB.NET.Devices.Corsair;
using RGB.NET.Devices.Logitech;
using RGB.NET.Devices.Msi;
using RGB.NET.Devices.Novation;
using RGB.NET.Devices.PicoPi;
using RGB.NET.Devices.Razer;
using RGB.NET.Devices.SteelSeries;
using RGB.NET.Devices.Wooting;

public class LightingServer
{
    private volatile bool _isRunning = false;
    private CancellationTokenSource _cts = new();

    private Thread? _handshakeThread,
        _udpListenerThread,
        _commandListenerThread,
        _discoveryThread;

    private TcpListener? _handshakeListener;
    private UdpClient? _udpClient;
    private TcpListener? _commandListener;
    private UdpClient? _discoveryClient;

    private readonly RGBSurface _surface;
    private readonly Dictionary<int, Led> _ledIdMap = new();
    private readonly Action _shutdownAction;
    private readonly object _deviceLock = new object();

    private ServerConfig _config;
    private readonly string _configPath;

    private readonly List<IRGBDeviceProvider> _allProviders;

    public LightingServer(Action shutdownAction)
    {
        _shutdownAction = shutdownAction;
        _surface = new RGBSurface();
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        _allProviders = new List<IRGBDeviceProvider>
        {
            MsiDeviceProvider.Instance,
            CorsairDeviceProvider.Instance,
            LogitechDeviceProvider.Instance,
            AsusDeviceProvider.Instance,
            RazerDeviceProvider.Instance,
            WootingDeviceProvider.Instance,
            SteelSeriesDeviceProvider.Instance,
            NovationDeviceProvider.Instance,
            PicoPiDeviceProvider.Instance,
        };

        _config = LoadConfig();
    }

    private ServerConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                Console.WriteLine($"[Config] Loading configuration from {_configPath}");
                string json = File.ReadAllText(_configPath);
                var config =
                    JsonConvert.DeserializeObject<ServerConfig>(json) ?? new ServerConfig();

                if (config.EnabledIntegrations == null || !config.EnabledIntegrations.Any())
                {
                    Console.WriteLine("[Config] No integrations specified, enabling defaults.");
                    config.EnabledIntegrations = new List<string>
                    {
                        "Corsair",
                        "Logitech",
                        "Asus",
                        "Razer",
                        "Wooting",
                        "SteelSeries",
                        "Msi",
                    };
                }
                if (config.DisabledDevices == null)
                    config.DisabledDevices = new List<string>();
                return config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[Config] Error loading config, using defaults. Error: {ex.Message}"
            );
        }

        Console.WriteLine("[Config] No config file found, creating a default one.");
        return new ServerConfig
        {
            EnabledIntegrations = new List<string>
            {
                "Corsair",
                "Logitech",
                "Asus",
                "Razer",
                "Wooting",
                "SteelSeries",
                "Msi",
            },
            DisabledDevices = new List<string>(),
        };
    }

    private void SaveConfig()
    {
        try
        {
            string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
            File.WriteAllText(_configPath, json);
            Console.WriteLine($"[Config] Configuration saved to {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] Error saving config: {ex.Message}");
        }
    }

    public void Start()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();

        ReconfigureSurfaceFromConfig();

        _handshakeListener = new TcpListener(IPAddress.Any, 63211);
        _handshakeListener.Start();

        _udpClient = new UdpClient(63212);
        Console.WriteLine("[Server] UDP lighting listener on port 63212.");

        _commandListener = new TcpListener(IPAddress.Any, 63213);
        _commandListener.Start();
        Console.WriteLine("[Server] Command server listening on port 63213.");

        _discoveryClient = new UdpClient { EnableBroadcast = true };
        Console.WriteLine("[Server] Discovery broadcast started on port 63214.");

        _handshakeThread = new Thread(() => HandshakeServerLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Handshake",
        };
        _udpListenerThread = new Thread(() => UdpServerLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "UDP Listener",
        };
        _commandListenerThread = new Thread(() => CommandServerLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Command Listener",
        };
        _discoveryThread = new Thread(() => DiscoveryBroadcastLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Discovery",
        };

        _handshakeThread.Start();
        _udpListenerThread.Start();
        _commandListenerThread.Start();
        _discoveryThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _cts.Cancel();

        try
        {
            _handshakeListener?.Stop();
        }
        catch { }
        try
        {
            _commandListener?.Stop();
        }
        catch { }
        try
        {
            _udpClient?.Close();
        }
        catch { }
        try
        {
            _discoveryClient?.Close();
        }
        catch { }

        Join(_handshakeThread);
        Join(_udpListenerThread);
        Join(_commandListenerThread);
        Join(_discoveryThread);

        lock (_deviceLock)
        {
            var devicesToDetach = _surface.Devices.ToList();
            if (devicesToDetach.Count > 0)
            {
                try
                {
                    _surface.Detach(devicesToDetach);
                }
                catch { }
            }

            foreach (var provider in _allProviders)
            {
                try
                {
                    if (provider.IsInitialized && provider is IDisposable d)
                        d.Dispose();
                }
                catch { }
            }
        }

        try
        {
            _surface.Dispose();
        }
        catch { }
    }

    private static void Join(Thread? t, int ms = 3000)
    {
        if (t == null)
            return;
        try
        {
            if (t.IsAlive)
                t.Join(ms);
        }
        catch { }
    }

    private void ReconfigureSurfaceFromConfig()
    {
        lock (_deviceLock)
        {
            Console.WriteLine("[Server] Configuring surface based on settings...");

            var currentDevices = _surface.Devices.ToList();
            if (currentDevices.Any())
                _surface.Detach(currentDevices);

            foreach (var provider in _allProviders)
            {
                string providerName = provider.GetType().Name.Replace("DeviceProvider", "");
                bool shouldBeEnabled = _config.EnabledIntegrations.Contains(providerName);

                if (shouldBeEnabled && !provider.IsInitialized)
                {
                    if (provider is MsiDeviceProvider && !Program.IsRunningAsAdmin())
                    {
                        Console.WriteLine(
                            $"[Server] -> Skipping {providerName}: Not running as admin."
                        );
                        continue;
                    }
                    try
                    {
                        Console.WriteLine($"[Server] Initializing {providerName}...");
                        provider.Initialize(throwExceptions: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[Server] -> WARNING: Initializing {providerName} failed. Details: {ex}"
                        );
                    }
                }
                else if (!shouldBeEnabled && provider.IsInitialized)
                {
                    Console.WriteLine($"[Server] Disposing disabled provider: {providerName}");
                    try
                    {
                        provider.Dispose();
                    }
                    catch { }
                }
            }

            var devicesToAttach = _allProviders
                .Where(p => p.IsInitialized)
                .SelectMany(p => p.Devices)
                .Where(d =>
                    !_config.DisabledDevices.Contains(
                        $"{d.DeviceInfo.Manufacturer}|{d.DeviceInfo.Model}"
                    )
                )
                .ToList();

            if (devicesToAttach.Any())
                _surface.Attach(devicesToAttach);

            Console.WriteLine($"[RGB.NET] Final device count: {_surface.Devices.Count()}");
            BuildIdMaps();
        }
    }

    private void BuildIdMaps()
    {
        _ledIdMap.Clear();
        int currentLedId = 0;

        foreach (var device in _surface.Devices)
        {
            foreach (var led in device)
                _ledIdMap.Add(currentLedId++, led);
        }
        Console.WriteLine($"[Server] Mapped {_ledIdMap.Count} total LEDs across all devices.");
    }

    private void HandshakeServerLoop(CancellationToken ct)
    {
        var listener = _handshakeListener!;
        try
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();

                var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                int clientConfigLength = IPAddress.NetworkToHostOrder(reader.ReadInt32());
                byte[] clientConfigBytes = reader.ReadBytes(clientConfigLength);
                string clientConfigJson = Encoding.UTF8.GetString(clientConfigBytes);
                JObject clientConfig = JObject.Parse(clientConfigJson);

                _config.EnabledIntegrations =
                    clientConfig["enabled_integrations"]?.ToObject<List<string>>()
                    ?? _config.EnabledIntegrations;

                _config.DisabledDevices =
                    clientConfig["disabled_devices"]?.ToObject<List<string>>()
                    ?? _config.DisabledDevices;

                SaveConfig();
                ReconfigureSurfaceFromConfig();

                lock (_deviceLock)
                {
                    var devicesArray = new JArray();
                    foreach (
                        var device in _surface
                            .Devices.OrderBy(d => d.DeviceInfo.DeviceType)
                            .ThenBy(d => d.DeviceInfo.Model)
                    )
                    {
                        var deviceKeyMap = new JObject();
                        var deviceLeds = new List<int>();

                        foreach (var led in device)
                        {
                            var mapping = _ledIdMap.FirstOrDefault(x => x.Value == led);
                            if (mapping.Value == null)
                                continue;
                            int ledId = mapping.Key;
                            deviceLeds.Add(ledId);

                            string? keyName = KeyMapper.GetFriendlyName(led.Id);
                            if (keyName == null)
                                keyName = led.Id.ToString().ToUpper().Replace("KEYBOARD_", "");

                            if (!deviceKeyMap.ContainsKey(keyName))
                                deviceKeyMap.Add(keyName, ledId);
                        }

                        if (!deviceLeds.Any())
                            continue;

                        var deviceObj = new JObject
                        {
                            ["sdk"] = device.DeviceInfo.Manufacturer,
                            ["name"] = device.DeviceInfo.Model,
                            ["leds"] = new JArray(deviceLeds),
                            ["key_map"] = deviceKeyMap,
                        };
                        devicesArray.Add(deviceObj);
                    }

                    var response = new JObject { ["devices"] = devicesArray };
                    byte[] dataBytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    int networkOrderLength = IPAddress.HostToNetworkOrder(dataBytes.Length);
                    byte[] lengthBytes = BitConverter.GetBytes(networkOrderLength);

                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(dataBytes, 0, dataBytes.Length);
                    stream.Flush();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch { }
        }
    }

    private void UdpServerLoop(CancellationToken ct)
    {
        var udpClient = _udpClient!;
        var from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                if (udpClient.Available == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                byte[] recvBuffer = udpClient.Receive(ref from);
                string jsonString = Encoding.UTF8.GetString(recvBuffer);

                JObject? frameData = null;
                try
                {
                    frameData = JObject.Parse(jsonString);
                }
                catch { }

                if (frameData?["led_colors"] is JArray colors)
                {
                    foreach (var item in colors)
                    {
                        int id = item.Value<int>("id");
                        int r = item.Value<int>("r");
                        int g = item.Value<int>("g");
                        int b = item.Value<int>("b");
                        if (_ledIdMap.TryGetValue(id, out Led led))
                            led.Color = new RGB.NET.Core.Color((byte)r, (byte)g, (byte)b);
                    }
                    _surface.Update();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private void CommandServerLoop(CancellationToken ct)
    {
        var listener = _commandListener!;
        try
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();

                var buffer = new byte[1024];
                int bytesRead = 0;
                try
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    bytesRead = 0;
                }

                if (bytesRead <= 0)
                    continue;

                string command = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (string.Equals(command, "restart_admin", StringComparison.OrdinalIgnoreCase))
                    Program.RequestRestart(true);
                else if (string.Equals(command, "restart", StringComparison.OrdinalIgnoreCase))
                    Program.RequestRestart(false);
                else if (string.Equals(command, "shutdown", StringComparison.OrdinalIgnoreCase))
                    _shutdownAction?.Invoke();
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch { }
        }
    }

    private void DiscoveryBroadcastLoop(CancellationToken ct)
    {
        var client = _discoveryClient!;
        var endpoint = new IPEndPoint(IPAddress.Broadcast, 63214);
        byte[] message = Encoding.UTF8.GetBytes("MINELIGHTS_PROXY_HELLO");

        try
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    client.Send(message, message.Length, endpoint);
                }
                catch (SocketException)
                {
                    break;
                }
                Thread.Sleep(5000);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }
}
