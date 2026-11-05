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
    private V2ProtocolServer? _v2Protocol;
    private readonly Dictionary<string, Dictionary<uint, Led>> _v2LedMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, RGB.NET.Core.Color>> _v2DefaultColors = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _v2DeviceByGlobalLed = new();
    private readonly object _v2MapLock = new();
    private readonly StableDeviceIdRegistry _deviceIdRegistry = new();

    private Thread? _handshakeThread,
        _presenceProbeThread,
        _udpListenerThread,
        _discoveryThread;

    private TcpListener? _handshakeListener;
    private TcpListener? _presenceProbeListener;
    private UdpClient? _udpClient;
    private UdpClient? _discoveryClient;

    private readonly RGBSurface _surface;
    private readonly Dictionary<int, Led> _ledIdMap = new();
    private readonly object _deviceLock = new object();

    private ServerConfig _config;
    private readonly string _configPath;

    private readonly List<IRGBDeviceProvider> _allProviders;

    private bool _isEmulationMode = false;
    private List<EmulatedDevice>? _emulatedDevices;
    private Dictionary<int, EmulatedLed>? _emulatedLedMap;
    private long _lastLegacyPromptAtUtcTicks;
    private int _deviceRefreshQueued;
    private long _deviceLayoutRevision;

    public LightingServer()
    {
        CorsairDeviceProvider.ExclusiveAccess = true;
        DeviceDefinitions.Register();

        _surface = new RGBSurface();
        _surface.SurfaceLayoutChanged += OnSurfaceLayoutChanged;
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

    public RawSnapshotFile BuildRawSnapshot()
    {
        lock (_deviceLock)
        {
            var snapshot = new RawSnapshotFile();

            foreach (var device in _surface.Devices)
            {
                var dev = new RawDeviceSnapshot
                {
                    Manufacturer = device.DeviceInfo.Manufacturer,
                    Model = device.DeviceInfo.Model,
                    DeviceType = device.DeviceInfo.DeviceType.ToString(),
                    DeviceName = device.DeviceInfo.DeviceName
                };

                foreach (var led in device)
                {
                    var abs = led.AbsoluteBoundary;

                    dev.Leds.Add(new RawLedSnapshot
                    {
                        LedId = led.Id.ToString(),

                        A = led.Color.A,
                        R = led.Color.R,
                        G = led.Color.G,
                        B = led.Color.B,

                        LocationX = led.Location.X,
                        LocationY = led.Location.Y,

                        SizeWidth = led.Size.Width,
                        SizeHeight = led.Size.Height,

                        AbsX = abs.Location.X,
                        AbsY = abs.Location.Y,
                        AbsWidth = abs.Size.Width,
                        AbsHeight = abs.Size.Height,

                        Shape = led.Shape.ToString(),
                        ShapeData = led.ShapeData
                    });
                }

                snapshot.Devices.Add(dev);
            }

            return snapshot;
        }
    }

    public class RawSnapshotFile
    {
        public List<RawDeviceSnapshot> Devices { get; set; } = new();
    }

    public class RawDeviceSnapshot
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string DeviceType { get; set; }
        public string DeviceName { get; set; }

        public List<RawLedSnapshot> Leds { get; set; } = new();
    }

    public class RawLedSnapshot
    {
        public string LedId { get; set; }

        public float A { get; set; }
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }

        public float LocationX { get; set; }
        public float LocationY { get; set; }

        public float SizeWidth { get; set; }
        public float SizeHeight { get; set; }

        public float AbsX { get; set; }
        public float AbsY { get; set; }
        public float AbsWidth { get; set; }
        public float AbsHeight { get; set; }

        public string Shape { get; set; }
        public string? ShapeData { get; set; }
    }

    public void LoadSnapshotForEmulation(RawSnapshotFile snapshot)
    {
        lock (_deviceLock)
        {
            _isEmulationMode = true;

            _emulatedDevices = snapshot.Devices
                .Select(d => new EmulatedDevice
                {
                    Manufacturer = d.Manufacturer,
                    Model = d.Model,
                    DeviceType = d.DeviceType,
                    DeviceName = d.DeviceName,
                    Leds = d.Leds.Select(l => new EmulatedLed
                    {
                        LedId = l.LedId,
                        A = l.A,
                        R = l.R,
                        G = l.G,
                        B = l.B,
                        LocationX = l.LocationX,
                        LocationY = l.LocationY,
                        SizeWidth = l.SizeWidth,
                        SizeHeight = l.SizeHeight,
                        AbsX = l.AbsX,
                        AbsY = l.AbsY,
                        AbsWidth = l.AbsWidth,
                        AbsHeight = l.AbsHeight,
                        Shape = l.Shape,
                        ShapeData = l.ShapeData
                    }).ToList()
                })
                .ToList();

            _emulatedLedMap = new Dictionary<int, EmulatedLed>();
            int id = 0;
            foreach (var dev in _emulatedDevices)
                foreach (var led in dev.Leds)
                    _emulatedLedMap[id++] = led;

            Console.WriteLine("[Emulation] Snapshot loaded. Emulation mode active.");
        }
    }

    public class EmulatedDevice
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string DeviceType { get; set; }
        public string DeviceName { get; set; }

        public List<EmulatedLed> Leds { get; set; } = new();
    }

    public class EmulatedLed
    {
        public string LedId { get; set; }
        public float A { get; set; }
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }

        public float LocationX { get; set; }
        public float LocationY { get; set; }
        public float SizeWidth { get; set; }
        public float SizeHeight { get; set; }

        public float AbsX { get; set; }
        public float AbsY { get; set; }
        public float AbsWidth { get; set; }
        public float AbsHeight { get; set; }

        public string Shape { get; set; }
        public string? ShapeData { get; set; }
    }

    public void ExitEmulation()
    {
        lock (_deviceLock)
        {
            if (!_isEmulationMode)
                return;

            _isEmulationMode = false;
            _emulatedDevices = null;
            _emulatedLedMap = null;

            ReconfigureSurfaceFromConfig();

            Console.WriteLine("[Emulation] Emulation mode exited. Real hardware restored.");
        }
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

        _v2Protocol = new V2ProtocolServer(GetV2DeviceList, ApplyV2Frame, RestoreV2Devices);
        _v2Protocol.Start();
        if (_config.RemoteAccessEnabled)
        {
            var configuredAddresses = (_config.RemoteListenAddresses ?? new List<string>())
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .Where(address => address != null)
                .Cast<IPAddress>()
                .ToArray();
            try
            {
                if (configuredAddresses.Length > 0)
                    _v2Protocol.ConfigureRemoteAccess(configuredAddresses, _config.RemotePort);
                else
                    Console.WriteLine("[V2] Remote access was enabled but has no saved interfaces; using local-only mode.");
            }
            catch (Exception error)
            {
                Console.WriteLine($"[V2] Could not restore remote listener; using local-only mode: {error.Message}");
            }
        }
        StartLegacyPresenceProbe();
        StartLegacyHandshakeDetector();
        if (_config.AllowLegacyLocalClients)
            StartLegacyLightingListener();
    }

    public event Action? LegacyClientDetected;

    public bool AllowLegacyLocalClients => _config.AllowLegacyLocalClients;
    public bool RemoteAccessEnabled => _v2Protocol?.RemoteEnabled == true;
    public IReadOnlyList<string> RemoteListenAddresses => _config.RemoteListenAddresses ?? new List<string>();
    public int RemotePort => _v2Protocol?.RemoteEnabled == true ? _v2Protocol.Port : _config.RemotePort;

    public void ConfigureRemoteAccess(IEnumerable<IPAddress> addresses, int port)
    {
        var selected = addresses.Distinct().ToArray();
        if (selected.Length == 0)
        {
            _v2Protocol?.DisableRemoteAccess();
            _config.RemoteAccessEnabled = false;
            SaveConfig();
            return;
        }
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1 to 65535.");
        if (_v2Protocol == null)
            throw new InvalidOperationException("The lighting server is not running.");

        _v2Protocol.ConfigureRemoteAccess(selected, port);
        if (!_v2Protocol.RemoteEnabled)
            throw new IOException("Remote access could not be enabled; the loopback V2 service remains available.");
        _config.RemoteAccessEnabled = true;
        _config.RemoteListenAddresses = _v2Protocol.RemoteAddresses.Select(address => address.ToString()).ToList();
        _config.RemotePort = port;
        SaveConfig();
    }

    public IReadOnlyList<JObject> ListV2Clients() => _v2Protocol?.ListClients() ?? Array.Empty<JObject>();

    public void RevokeV2Client(string clientId) => _v2Protocol?.RevokeClient(clientId);

    public void SetV2ClientPriority(string clientId, int priority) => _v2Protocol?.SetClientPriority(clientId, priority);

    public string ExportV2PairingFile(string path, IPAddress? address = null)
        => _v2Protocol?.ExportPairingFile(path, address)
            ?? throw new InvalidOperationException("The lighting server is not running.");

    public void SetLegacyLocalCompatibility(bool allow)
    {
        if (_config.AllowLegacyLocalClients == allow)
            return;
        _config.AllowLegacyLocalClients = allow;
        SaveConfig();
        if (allow)
            StartLegacyLightingListener();
        else
            StopLegacyLightingListener();
    }

    private void StartLegacyHandshakeDetector()
    {
        try
        {
            _handshakeListener = new TcpListener(IPAddress.Loopback, 63211);
            _handshakeListener.Start();
            _handshakeThread = new Thread(() => HandshakeServerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "Legacy V1 Handshake Detector",
            };
            _handshakeThread.Start();
            Console.WriteLine("[V1] Loopback handshake detector listening on 63211.");
        }
        catch (SocketException error)
        {
            Console.WriteLine($"[V1] Legacy handshake detector unavailable on 127.0.0.1:63211: {error.Message}. V2 remains available.");
            try { _handshakeListener?.Stop(); } catch { }
            _handshakeListener = null;
        }
    }

    private void StartLegacyPresenceProbe()
    {
        try
        {
            _presenceProbeListener = new TcpListener(IPAddress.Loopback, 63213);
            _presenceProbeListener.Start();
            _presenceProbeThread = new Thread(() => LegacyPresenceProbeLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "Legacy V1 Presence Probe",
            };
            _presenceProbeThread.Start();
            Console.WriteLine("[V1] Loopback presence probe listening on 63213; legacy commands are rejected.");
        }
        catch (SocketException error)
        {
            Console.WriteLine($"[V1] Legacy presence probe unavailable on 127.0.0.1:63213: {error.Message}. V2 remains available.");
            try { _presenceProbeListener?.Stop(); } catch { }
            _presenceProbeListener = null;
        }
    }

    private void LegacyPresenceProbeLoop(CancellationToken cancellationToken)
    {
        var listener = _presenceProbeListener!;
        try
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                // Legacy clients only probe whether this port accepts a TCP connection.
                // Closing immediately preserves discovery without reading or executing V1 commands.
                using var client = listener.AcceptTcpClient();
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private void StartLegacyLightingListener()
    {
        if (!_config.AllowLegacyLocalClients || _udpClient != null)
            return;
        try
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 63212));
            _udpListenerThread = new Thread(() => UdpServerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "Legacy V1 UDP Listener",
            };
            _udpListenerThread.Start();
            _discoveryClient = new UdpClient { EnableBroadcast = true };
            _discoveryThread = new Thread(() => DiscoveryBroadcastLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "Legacy V1 Discovery",
            };
            _discoveryThread.Start();
            Console.WriteLine("[V1] Explicit loopback lighting compatibility enabled on UDP 63212; V2 remains authenticated.");
        }
        catch (SocketException error)
        {
            Console.WriteLine($"[V1] Could not bind legacy lighting port 63212: {error.Message}. V2 remains available.");
            try { _udpClient?.Close(); } catch { }
            try { _discoveryClient?.Close(); } catch { }
            _udpClient = null;
            _discoveryClient = null;
        }
    }

    private void StopLegacyLightingListener()
    {
        try { _udpClient?.Close(); } catch { }
        try { _discoveryClient?.Close(); } catch { }
        _udpClient = null;
        _discoveryClient = null;
        Join(_udpListenerThread, 1000);
        Join(_discoveryThread, 1000);
        _udpListenerThread = null;
        _discoveryThread = null;
        Console.WriteLine("[V1] Legacy local lighting compatibility disabled. V2 remains available.");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts.Cancel();
        try { _v2Protocol?.Dispose(); } catch { }
        _v2Protocol = null;

        try
        {
            _handshakeListener?.Stop();
        }
        catch { }
        try
        {
            _presenceProbeListener?.Stop();
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
        Join(_presenceProbeThread);
        Join(_udpListenerThread);
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
        RefreshV2DeviceMaps();
    }

    private void OnSurfaceLayoutChanged(SurfaceLayoutChangedEventArgs args)
    {
        if ((!args.DeviceAdded && !args.DeviceRemoved) || !_isRunning)
            return;
        Interlocked.Increment(ref _deviceLayoutRevision);
        QueueDeviceRefresh();
    }

    private void QueueDeviceRefresh()
    {
        if (Interlocked.Exchange(ref _deviceRefreshQueued, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            long processedRevision = -1;
            try
            {
                do
                {
                    processedRevision = Interlocked.Read(ref _deviceLayoutRevision);
                    await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                    lock (_deviceLock)
                        BuildIdMaps();
                    _v2Protocol?.PublishDeviceListChanged();
                }
                while (processedRevision != Interlocked.Read(ref _deviceLayoutRevision));
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Console.WriteLine($"[V2] Could not refresh the device list after a hardware change: {error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _deviceRefreshQueued, 0);
                if (_isRunning && processedRevision != Interlocked.Read(ref _deviceLayoutRevision))
                    QueueDeviceRefresh();
            }
        });
    }

    private void NotifyLegacyClientAttempt()
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastLegacyPromptAtUtcTicks);
        if (now - previous < TimeSpan.FromSeconds(20).Ticks
            || Interlocked.CompareExchange(ref _lastLegacyPromptAtUtcTicks, now, previous) != previous)
            return;
        Console.WriteLine("[V1] Detected an unauthenticated legacy client. It was denied; enable legacy local compatibility from the tray menu to permit lighting access.");
        LegacyClientDetected?.Invoke();
    }

    private JArray GetV2DeviceList()
    {
        lock (_v2MapLock)
        {
            var devices = new JArray();
            if (_isEmulationMode && _emulatedDevices != null)
            {
                var ids = ResolveEmulatedDeviceIds(_emulatedDevices);
                foreach (var device in _emulatedDevices)
                {
                    var deviceId = ids[device];
                    var elements = new JArray();
                    for (uint index = 0; index < device.Leds.Count; index++)
                        elements.Add(new JObject { ["element_id"] = index, ["key"] = device.Leds[(int)index].LedId });
                    devices.Add(new JObject
                    {
                        ["device_id"] = deviceId,
                        ["name"] = device.DeviceName,
                        ["vendor"] = device.Manufacturer,
                        ["integration"] = device.Manufacturer,
                        ["elements"] = elements,
                        ["capabilities"] = EmulatedCapabilities(device.DeviceType)
                    });
                }
                return devices;
            }

            var realDevices = _surface.Devices.OrderBy(d => d.DeviceInfo.DeviceType).ThenBy(d => d.DeviceInfo.DeviceName).ToArray();
            var realIds = ResolveRealDeviceIds(realDevices);
            foreach (var device in realDevices)
            {
                var deviceId = realIds[device];
                var elements = new JArray();
                uint index = 0;
                foreach (var led in device)
                {
                    var key = KeyMapper.GetFriendlyName(led.Id) ?? led.Id.ToString().ToUpperInvariant();
                    elements.Add(new JObject { ["element_id"] = index++, ["key"] = key });
                }
                devices.Add(new JObject
                {
                    ["device_id"] = deviceId,
                    ["name"] = device.DeviceInfo.DeviceName,
                    ["vendor"] = device.DeviceInfo.Manufacturer,
                    ["integration"] = device.DeviceInfo.Manufacturer,
                    ["elements"] = elements,
                    ["capabilities"] = DeviceCapabilities(device.DeviceInfo.DeviceType)
                });
            }
            return devices;
        }
    }

    private void RefreshV2DeviceMaps()
    {
        lock (_v2MapLock)
        {
            _v2LedMap.Clear();
            _v2DefaultColors.Clear();
            _v2DeviceByGlobalLed.Clear();
            if (_isEmulationMode && _emulatedDevices != null && _emulatedLedMap != null)
            {
                var ids = ResolveEmulatedDeviceIds(_emulatedDevices);
                foreach (var device in _emulatedDevices)
                {
                    var deviceId = ids[device];
                    var ledMap = new Dictionary<uint, Led>();
                    var defaults = new Dictionary<uint, RGB.NET.Core.Color>();
                    for (uint index = 0; index < device.Leds.Count; index++)
                    {
                        var emulated = device.Leds[(int)index];
                        var globalId = _emulatedLedMap.FirstOrDefault(pair => ReferenceEquals(pair.Value, emulated)).Key;
                        _v2DeviceByGlobalLed[globalId] = deviceId;
                    }
                    _v2LedMap[deviceId] = ledMap;
                    _v2DefaultColors[deviceId] = defaults;
                }
                return;
            }

            var realDevices = _surface.Devices.ToArray();
            var realIds = ResolveRealDeviceIds(realDevices);
            foreach (var device in realDevices)
            {
                var deviceId = realIds[device];
                var ledMap = new Dictionary<uint, Led>();
                var defaults = new Dictionary<uint, RGB.NET.Core.Color>();
                uint index = 0;
                foreach (var led in device)
                {
                    ledMap[index] = led;
                    defaults[index] = led.Color;
                    var globalId = _ledIdMap.FirstOrDefault(pair => ReferenceEquals(pair.Value, led)).Key;
                    _v2DeviceByGlobalLed[globalId] = deviceId;
                    index++;
                }
                _v2LedMap[deviceId] = ledMap;
                _v2DefaultColors[deviceId] = defaults;
            }
        }
    }

    private void ApplyV2Frame(JObject frame)
    {
        lock (_deviceLock)
        lock (_v2MapLock)
        {
            var changed = false;
            if (frame["targets"] is not JArray targets)
                return;
            foreach (var target in targets.OfType<JObject>())
            {
                var deviceId = target.Value<string>("device_id");
                if (deviceId == null || !_v2LedMap.TryGetValue(deviceId, out var ledMap) || target["colors"] is not JArray colors)
                    continue;
                foreach (var color in colors.OfType<JObject>())
                {
                    if (!uint.TryParse(color["element_id"]?.ToString(), out var elementId) || !ledMap.TryGetValue(elementId, out var led))
                        continue;
                    var red = color.Value<int>("r");
                    var green = color.Value<int>("g");
                    var blue = color.Value<int>("b");
                    if (_isEmulationMode && _emulatedDevices != null && _emulatedLedMap != null)
                    {
                        var globalId = _ledIdMap.FirstOrDefault(pair => ReferenceEquals(pair.Value, led)).Key;
                        if (_emulatedLedMap.TryGetValue(globalId, out var emulated))
                        {
                            emulated.R = red / 255f;
                            emulated.G = green / 255f;
                            emulated.B = blue / 255f;
                        }
                    }
                    else
                    {
                        led.Color = new RGB.NET.Core.Color((byte)red, (byte)green, (byte)blue);
                        changed = true;
                    }
                }
            }
            if (changed)
                _surface.Update();
        }
    }

    private void RestoreV2Devices(IReadOnlyCollection<string> deviceIds)
    {
        lock (_deviceLock)
        lock (_v2MapLock)
        {
            foreach (var deviceId in deviceIds)
            {
                if (!_v2LedMap.TryGetValue(deviceId, out var leds) || !_v2DefaultColors.TryGetValue(deviceId, out var defaults))
                    continue;
                foreach (var pair in leds)
                {
                    if (defaults.TryGetValue(pair.Key, out var color))
                        pair.Value.Color = color;
                }
            }
            if (!_isEmulationMode)
                _surface.Update();
        }
    }

    private Dictionary<object, string> ResolveRealDeviceIds(IEnumerable<IRGBDevice> devices)
    {
        var descriptors = devices.Select(device =>
        {
            var info = device.DeviceInfo;
            var identity = $"{info.Manufacturer}\0{info.DeviceType}\0{info.Model}";
            var hardwareId = TryGetHardwareIdentifier(info);
            var elementSignature = string.Join("\0", device.Select(led => led.Id.ToString()).OrderBy(value => value, StringComparer.Ordinal));
            var disambiguator = hardwareId != null ? "hardware:" + hardwareId : "name:" + info.DeviceName;
            return new StableDeviceIdRegistry.DeviceDescriptor(device, identity, disambiguator, info.DeviceName + "\0" + elementSignature);
        }).ToArray();
        return _deviceIdRegistry.Resolve(descriptors).ToDictionary(pair => pair.Key, pair => pair.Value, ReferenceEqualityComparer.Instance);
    }

    private Dictionary<object, string> ResolveEmulatedDeviceIds(IEnumerable<EmulatedDevice> devices)
    {
        var descriptors = devices.Select(device =>
        {
            var identity = $"{device.Manufacturer}\0{device.DeviceType}\0{device.Model}";
            var elementSignature = string.Join("\0", device.Leds.Select(led => led.LedId).OrderBy(value => value, StringComparer.Ordinal));
            return new StableDeviceIdRegistry.DeviceDescriptor(device, identity, "name:" + device.DeviceName, device.DeviceName + "\0" + elementSignature);
        }).ToArray();
        return _deviceIdRegistry.Resolve(descriptors).ToDictionary(pair => pair.Key, pair => pair.Value, ReferenceEqualityComparer.Instance);
    }

    private static string? TryGetHardwareIdentifier(IRGBDeviceInfo info)
    {
        foreach (var propertyName in new[] { "DeviceId", "SerialNumber", "Serial", "HardwareId", "UniqueId", "DevicePath", "Path" })
        {
            try
            {
                var property = info.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var value = property?.GetValue(info)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return propertyName + ":" + value;
            }
            catch { }
        }
        return null;
    }

    private static JArray DeviceCapabilities(RGBDeviceType deviceType)
    {
        var capabilities = new JArray("rgb");
        if ((deviceType & (RGBDeviceType.Keyboard | RGBDeviceType.Keypad)) != 0)
            capabilities.Add("keyboard-key-map");
        return capabilities;
    }

    private static JArray EmulatedCapabilities(string deviceType)
        => Enum.TryParse<RGBDeviceType>(deviceType, ignoreCase: true, out var parsed)
            ? DeviceCapabilities(parsed)
            : new JArray("rgb");

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
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using var stream = client.GetStream();
                    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                    int clientConfigLength = IPAddress.NetworkToHostOrder(reader.ReadInt32());
                    if (clientConfigLength <= 0 || clientConfigLength > 1_048_576)
                    {
                        Console.WriteLine("[Handshake] Ignoring client with an invalid config length.");
                        continue;
                    }

                    byte[] clientConfigBytes = reader.ReadBytes(clientConfigLength);
                    if (clientConfigBytes.Length != clientConfigLength)
                        throw new EndOfStreamException("Client disconnected before sending its full config.");

                    string clientConfigJson = Encoding.UTF8.GetString(clientConfigBytes);
                    _ = JObject.Parse(clientConfigJson);
                    if (!_config.AllowLegacyLocalClients)
                    {
                        NotifyLegacyClientAttempt();
                        continue;
                    }

                    lock (_deviceLock)
                    {
                        JArray devicesArray;

                        if (_isEmulationMode && _emulatedDevices != null)
                        {
                            devicesArray = new JArray();

                            foreach (var dev in _emulatedDevices)
                            {
                                var deviceKeyMap = new JObject();
                                var deviceLeds = new List<int>();

                                foreach (var led in dev.Leds)
                                {
                                    int ledId = _emulatedLedMap!.First(x => x.Value == led).Key;
                                    deviceLeds.Add(ledId);

                                    string keyName = led.LedId.ToUpper();
                                    if (!deviceKeyMap.ContainsKey(keyName))
                                        deviceKeyMap.Add(keyName, ledId);
                                }

                                devicesArray.Add(new JObject
                                {
                                    ["sdk"] = dev.Manufacturer,
                                    ["name"] = dev.Model,
                                    ["leds"] = new JArray(deviceLeds),
                                    ["key_map"] = deviceKeyMap
                                });
                            }
                        }
                        else
                        {
                            devicesArray = new JArray();

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
                catch (IOException ex)
                {
                    Console.WriteLine($"[Handshake] Ignoring incomplete client handshake: {ex.Message}");
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[Handshake] Ignoring disconnected client: {ex.Message}");
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[Handshake] Ignoring invalid client config: {ex.Message}");
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Console.WriteLine($"[Handshake] Ignoring invalid client handshake: {ex.Message}");
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
                    void ApplyFrame()
                    {
                        lock (_deviceLock)
                        {
                            var surfaceChanged = false;
                            foreach (var item in colors)
                            {
                                int id = item.Value<int>("id");
                                int r = item.Value<int>("r");
                                int g = item.Value<int>("g");
                                int b = item.Value<int>("b");
                                if (IsV2DeviceLeasedForLegacyLed(id)) continue;
                                if (_isEmulationMode)
                                {
                                    if (_emulatedLedMap != null && _emulatedLedMap.TryGetValue(id, out var emu))
                                    {
                                        emu.R = r / 255f;
                                        emu.G = g / 255f;
                                        emu.B = b / 255f;
                                    }
                                }
                                else if (_ledIdMap.TryGetValue(id, out Led led))
                                {
                                    led.Color = new RGB.NET.Core.Color((byte)r, (byte)g, (byte)b);
                                    surfaceChanged = true;
                                }
                            }
                            if (surfaceChanged) _surface.Update();
                        }
                    }
                    var protocol = _v2Protocol;
                    if (protocol == null) ApplyFrame();
                    else protocol.ApplyLegacyFrame(ApplyFrame);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private bool IsV2DeviceLeasedForLegacyLed(int globalLedId)
    {
        lock (_v2MapLock)
        {
            return _v2DeviceByGlobalLed.TryGetValue(globalLedId, out var deviceId)
                && _v2Protocol?.IsDeviceLeased(deviceId) == true;
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
