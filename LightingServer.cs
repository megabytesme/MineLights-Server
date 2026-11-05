using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
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
    private const int CurrentConfigSchemaVersion = 2;
    private static readonly string[] DefaultProviders =
        { "Corsair", "Logitech", "Asus", "Razer", "Wooting", "SteelSeries", "Msi", "Novation", "PicoPi" };
    private volatile bool _isRunning = false;
    private CancellationTokenSource _cts = new();
    private V2ProtocolServer? _v2Protocol;
    private readonly Dictionary<string, Dictionary<uint, Led>> _v2LedMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, EmulatedLed>> _v2EmulatedLedMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, RGB.NET.Core.Color>> _v2DefaultColors = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _v2DeviceByGlobalLed = new();
    private readonly object _v2MapLock = new();
    private readonly Dictionary<IRGBDevice, string> _providerByDevice = new(ReferenceEqualityComparer.Instance);
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
    private readonly Dictionary<Led, int> _legacyIdsByLed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Led, RGB.NET.Core.Color> _originalColorsByLed = new(ReferenceEqualityComparer.Instance);
    private HashSet<IRGBDevice> _knownSurfaceDevices = new(ReferenceEqualityComparer.Instance);
    private long _nextLegacyLedId;
    private readonly object _deviceLock = new object();

    private volatile ServerConfig _config;
    private readonly string _configPath;
    private readonly object _configLock = new();

    private readonly Dictionary<string, Func<IRGBDeviceProvider>> _providerFactories = new(StringComparer.Ordinal)
    {
        ["Msi"] = () => MsiDeviceProvider.Instance,
        ["Corsair"] = () => CorsairDeviceProvider.Instance,
        ["Logitech"] = () => LogitechDeviceProvider.Instance,
        ["Asus"] = () => AsusDeviceProvider.Instance,
        ["Razer"] = () => RazerDeviceProvider.Instance,
        ["Wooting"] = () => WootingDeviceProvider.Instance,
        ["SteelSeries"] = () => SteelSeriesDeviceProvider.Instance,
        ["Novation"] = () => NovationDeviceProvider.Instance,
        ["PicoPi"] = () => PicoPiDeviceProvider.Instance,
    };
    private readonly Dictionary<string, IRGBDeviceProvider> _providers = new(StringComparer.Ordinal);
    private readonly object _interestLock = new();
    private readonly Dictionary<string, HashSet<string>?> _v2ProviderInterests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>?> _recentV2ProviderInterests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _legacyProviderInterests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingLegacyProviderInterests = new(StringComparer.Ordinal);
    private long _providerInterestRevision;
    private int _providerUpdateQueued;

    private bool _isEmulationMode = false;
    private List<EmulatedDevice>? _emulatedDevices;
    private Dictionary<int, EmulatedLed>? _emulatedLedMap;
    private Dictionary<EmulatedLed, int>? _emulatedIdsByLed;
    private Dictionary<int, RGB.NET.Core.Color>? _emulatedDefaultColors;
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
                    DeviceName = device.DeviceInfo.DeviceName,
                    Provider = GetProviderName(device)
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
        public string? Provider { get; set; }

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
        lock (_v2MapLock)
        {
            long ledCount = snapshot.Devices.Sum(device => (long)device.Leds.Count);
            if (_nextLegacyLedId + ledCount > (long)int.MaxValue + 1)
                throw new InvalidOperationException("Legacy LED ID space exhausted; restart MineLights Server.");
            _isEmulationMode = true;

            _emulatedDevices = snapshot.Devices
                .Select(d => new EmulatedDevice
                {
                    Manufacturer = d.Manufacturer,
                    Model = d.Model,
                    DeviceType = d.DeviceType,
                    DeviceName = d.DeviceName,
                    Provider = d.Provider,
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

            var unknownProviders = _emulatedDevices
                .Where(device => string.IsNullOrWhiteSpace(device.Provider)
                    && InferProviderName(device.Manufacturer) == null)
                .Select(device => $"{device.Manufacturer}|{device.Model}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (unknownProviders.Length > 0)
                Console.WriteLine("[Emulation] The snapshot lacks a known provider for: " + string.Join(", ", unknownProviders)
                    + ". Integration-filtered clients cannot use these devices. Create a new snapshot with the current server,"
                    + " or add the correct Provider field (for example Corsair, Asus, Msi, or PicoPi) to each device.");

            _emulatedLedMap = new Dictionary<int, EmulatedLed>();
            _emulatedIdsByLed = new Dictionary<EmulatedLed, int>(ReferenceEqualityComparer.Instance);
            _emulatedDefaultColors = new Dictionary<int, RGB.NET.Core.Color>();
            foreach (var dev in _emulatedDevices)
                foreach (var led in dev.Leds)
                {
                    int id = AllocateLegacyLedId();
                    _emulatedLedMap[id] = led;
                    _emulatedIdsByLed[led] = id;
                    _emulatedDefaultColors[id] = new RGB.NET.Core.Color(led.A, led.R, led.G, led.B);
                }

            RefreshV2DeviceMaps();

            Console.WriteLine("[Emulation] Snapshot loaded. Emulation mode active.");
        }
        _v2Protocol?.PublishDeviceListChanged();
    }

    public class EmulatedDevice
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string DeviceType { get; set; }
        public string DeviceName { get; set; }
        public string? Provider { get; set; }

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
        lock (_v2MapLock)
        {
            if (!_isEmulationMode)
                return;

            _isEmulationMode = false;
            _emulatedDevices = null;
            _emulatedLedMap = null;
            _emulatedIdsByLed = null;
            _emulatedDefaultColors = null;

            ReconfigureSurfaceFromConfig();

            Console.WriteLine("[Emulation] Emulation mode exited. Real hardware restored.");
        }
        _v2Protocol?.PublishDeviceListChanged();
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

                if (config.EnabledIntegrations == null)
                {
                    Console.WriteLine("[Config] No integrations specified, enabling defaults.");
                    config.EnabledIntegrations = DefaultProviders.ToList();
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
            ConfigSchemaVersion = CurrentConfigSchemaVersion,
            EnabledIntegrations = DefaultProviders.ToList(),
            DisabledDevices = new List<string>(),
        };
    }

    private ServerConfig CopyConfig()
    {
        return new ServerConfig
        {
            ConfigSchemaVersion = _config.ConfigSchemaVersion,
            EnabledIntegrations = new List<string>(_config.EnabledIntegrations),
            DisabledDevices = new List<string>(_config.DisabledDevices),
            AllowLegacyLocalClients = _config.AllowLegacyLocalClients,
            RemoteAccessEnabled = _config.RemoteAccessEnabled,
            RemoteListenAddresses = new List<string>(_config.RemoteListenAddresses ?? new List<string>()),
            RemotePort = _config.RemotePort,
        };
    }

    // Write a complete same-directory file before replacing the existing one.
    // Copy the existing Windows access rules to the staging file and fail closed
    // if the filesystem cannot perform the replacement.
    private void SaveConfig(ServerConfig config)
    {
        string temp = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(config, Formatting.Indented));
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            if (OperatingSystem.IsWindows() && File.Exists(_configPath))
            {
                var access = new FileInfo(_configPath).GetAccessControl(AccessControlSections.Access);
                new FileInfo(temp).SetAccessControl(access);
            }
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_configPath))
                File.Replace(temp, _configPath, destinationBackupFileName: null);
            else
                File.Move(temp, _configPath);
            Console.WriteLine($"[Config] Configuration saved to {_configPath}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException error) { Console.WriteLine($"[Config] Could not remove temporary config: {error.Message}"); }
        }
    }

    public void Start()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();

        ReconfigureSurfaceFromConfig();

        _v2Protocol = new V2ProtocolServer(GetV2DeviceList, ApplyV2Frame, RestoreV2Devices,
            legacyCompatibilityEnabled: _config.AllowLegacyLocalClients,
            updateProviderInterest: SetV2ProviderInterest, removeProviderInterest: RemoveV2ProviderInterest);
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
    public bool RequiresLegacyFilterReview => _config.ConfigSchemaVersion < CurrentConfigSchemaVersion;
    public (string[] Providers, string[] DisabledDevices) GetGlobalDeviceFilters()
    {
        lock (_configLock)
            return (_config.EnabledIntegrations.ToArray(), _config.DisabledDevices.ToArray());
    }

    public void ReviewGlobalDeviceFilters(bool reset)
    {
        lock (_configLock)
        {
            var updated = CopyConfig();
            if (reset)
            {
                updated.EnabledIntegrations = DefaultProviders.ToList();
                updated.DisabledDevices.Clear();
            }
            updated.ConfigSchemaVersion = CurrentConfigSchemaVersion;
            SaveConfig(updated);
            _config = updated;
        }
        if (reset && _isRunning)
        {
            if (ReconfigureSurfaceFromConfig()) _v2Protocol?.PublishDeviceListChanged();
        }
    }
    public bool RemoteAccessEnabled => _v2Protocol?.RemoteEnabled == true;
    public IReadOnlyList<string> RemoteListenAddresses => _config.RemoteListenAddresses ?? new List<string>();
    public int RemotePort => _v2Protocol?.RemoteEnabled == true ? _v2Protocol.Port : _config.RemotePort;

    public void ConfigureRemoteAccess(IEnumerable<IPAddress> addresses, int port)
    {
        var selected = addresses.Distinct().ToArray();
        lock (_configLock)
        {
        if (selected.Length == 0)
        {
            var updated = CopyConfig();
            updated.RemoteAccessEnabled = false;
            SaveConfig(updated);
            _config = updated;
            _v2Protocol?.DisableRemoteAccess();
            return;
        }
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1 to 65535.");
        if (_v2Protocol == null)
            throw new InvalidOperationException("The lighting server is not running.");

        var previousEnabled = _v2Protocol.RemoteEnabled;
        var previousAddresses = _v2Protocol.RemoteAddresses.ToArray();
        var previousPort = _config.RemotePort;
        try
        {
            _v2Protocol.ConfigureRemoteAccess(selected, port);
            if (!_v2Protocol.RemoteEnabled)
                throw new IOException("Remote access could not be enabled; the loopback V2 service remains available.");
            var updated = CopyConfig();
            updated.RemoteAccessEnabled = true;
            updated.RemoteListenAddresses = _v2Protocol.RemoteAddresses.Select(address => address.ToString()).ToList();
            updated.RemotePort = port;
            SaveConfig(updated);
            _config = updated;
        }
        catch
        {
            try
            {
                if (previousEnabled) _v2Protocol.ConfigureRemoteAccess(previousAddresses, previousPort);
                else _v2Protocol.DisableRemoteAccess();
            }
            catch (Exception error) { Console.WriteLine($"[V2] Could not restore previous listener: {error.Message}"); }
            throw;
        }
        }
    }

    public IReadOnlyList<JObject> ListV2Clients() => _v2Protocol?.ListClients() ?? Array.Empty<JObject>();

    public void RevokeV2Client(string clientId) => _v2Protocol?.RevokeClient(clientId);

    public void SetV2ClientPriority(string clientId, int priority) => _v2Protocol?.SetClientPriority(clientId, priority);

    public string ExportV2PairingFile(string path, IPAddress? address = null)
        => _v2Protocol?.ExportPairingFile(path, address)
            ?? throw new InvalidOperationException("The lighting server is not running.");

    public void SetLegacyLocalCompatibility(bool allow)
    {
        lock (_configLock)
        {
        if (_config.AllowLegacyLocalClients == allow)
            return;
        // Enabling V1 blocks V2 shutdown first; disabling V1 stops its
        // listener before allowing V2 shutdown again.
        if (allow) _v2Protocol?.SetLegacyCompatibilityEnabled(true);
        try
        {
            var updated = CopyConfig();
            updated.AllowLegacyLocalClients = allow;
            SaveConfig(updated);
            // Serialize the effective permission change with an in-flight V1
            // frame. Once this returns, no older frame can apply afterwards.
            lock (_deviceLock) _config = updated;
        }
        catch
        {
            if (allow) _v2Protocol?.SetLegacyCompatibilityEnabled(false);
            throw;
        }
        if (allow)
        {
            StartLegacyLightingListener();
            lock (_interestLock)
            {
                _legacyProviderInterests.UnionWith(_pendingLegacyProviderInterests);
                _pendingLegacyProviderInterests.Clear();
            }
            QueueProviderReconciliation();
        }
        else
        {
            StopLegacyLightingListener();
            lock (_interestLock)
            {
                _legacyProviderInterests.Clear();
                _pendingLegacyProviderInterests.Clear();
            }
            QueueProviderReconciliation();
            _v2Protocol?.SetLegacyCompatibilityEnabled(false);
        }
        }
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
            var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 63212));
            _udpClient = udpClient;
            _udpListenerThread = new Thread(() => UdpServerLoop(udpClient, _cts.Token))
            {
                IsBackground = true,
                Name = "Legacy V1 UDP Listener",
            };
            _udpListenerThread.Start();
            var discoveryClient = new UdpClient { EnableBroadcast = true };
            _discoveryClient = discoveryClient;
            _discoveryThread = new Thread(() => DiscoveryBroadcastLoop(discoveryClient, _cts.Token))
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
        var udpClient = _udpClient;
        var discoveryClient = _discoveryClient;
        _udpClient = null;
        _discoveryClient = null;
        try { udpClient?.Close(); } catch { }
        try { discoveryClient?.Close(); } catch { }
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

            foreach (var provider in _providers.Values)
            {
                try
                {
                    if (provider.IsInitialized && provider is IDisposable d)
                        d.Dispose();
                }
                catch { }
            }
            _providers.Clear();
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

    private void SetV2ProviderInterest(string sessionId, IReadOnlyList<string>? providers)
    {
        lock (_interestLock)
            _v2ProviderInterests[sessionId] = providers == null ? null : new HashSet<string>(providers, StringComparer.Ordinal);
        QueueProviderReconciliation();
    }

    private void RemoveV2ProviderInterest(string sessionId)
    {
        lock (_interestLock)
        {
            if (!_v2ProviderInterests.Remove(sessionId, out var providers)) return;
            // V2 clients may reconnect when a device list changes. Retain the
            // old interest briefly so that an SDK is not repeatedly unloaded.
            _recentV2ProviderInterests[sessionId] = providers;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            lock (_interestLock) _recentV2ProviderInterests.Remove(sessionId);
            QueueProviderReconciliation();
        });
    }

    private HashSet<string> RequestedProviders()
    {
        lock (_interestLock)
        {
            var requested = new HashSet<string>(_legacyProviderInterests, StringComparer.Ordinal);
            foreach (var providers in _v2ProviderInterests.Values.Concat(_recentV2ProviderInterests.Values))
            {
                if (providers == null) requested.UnionWith(DefaultProviders); // Older V2 clients did not declare interest.
                else requested.UnionWith(providers);
            }
            return requested;
        }
    }

    private void QueueProviderReconciliation()
    {
        Interlocked.Increment(ref _providerInterestRevision);
        if (!_isRunning || Interlocked.Exchange(ref _providerUpdateQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            long handled = -1;
            try
            {
                do
                {
                    handled = Interlocked.Read(ref _providerInterestRevision);
                    // Coalesce reconnects and rapid setting changes before releasing SDKs.
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                    if (!_isRunning) return;
                    if (ReconfigureSurfaceFromConfig()) _v2Protocol?.PublishDeviceListChanged();
                } while (handled != Interlocked.Read(ref _providerInterestRevision));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Console.WriteLine($"[Server] Provider update failed: {error.Message}"); }
            finally
            {
                Interlocked.Exchange(ref _providerUpdateQueued, 0);
                if (_isRunning && handled != Interlocked.Read(ref _providerInterestRevision)) QueueProviderReconciliation();
            }
        });
    }

    private bool SetLegacyProviderInterest(HashSet<string>? providers)
    {
        lock (_interestLock)
        {
            var target = _config.AllowLegacyLocalClients ? _legacyProviderInterests : _pendingLegacyProviderInterests;
            target.UnionWith(providers == null ? DefaultProviders : providers.Where(name => _providerFactories.ContainsKey(name)));
            if (!_config.AllowLegacyLocalClients) return false;
        }
        // Old V1 clients perform only one handshake, so complete discovery
        // before returning its layout instead of relying on a later event.
        if (ReconfigureSurfaceFromConfig()) _v2Protocol?.PublishDeviceListChanged();
        return true;
    }

    private bool ReconfigureSurfaceFromConfig()
    {
        var requested = RequestedProviders();
        lock (_deviceLock)
        {
            if (_isEmulationMode)
            {
                RefreshV2DeviceMaps();
                return false;
            }
            var ownerAllowed = new HashSet<string>(_config.EnabledIntegrations, StringComparer.Ordinal);
            requested.IntersectWith(ownerAllowed);
            foreach (var providerName in _providers.Keys.Where(name => !requested.Contains(name)).ToArray())
            {
                var provider = _providers[providerName];
                var attached = provider.Devices.Where(device => _surface.Devices.Contains(device)).ToArray();
                if (attached.Length > 0) _surface.Detach(attached);
                try { provider.Dispose(); }
                catch (Exception error) { Console.WriteLine($"[Server] Could not release {providerName}: {error.Message}"); }
                _providers.Remove(providerName);
            }
            foreach (var providerName in requested.Where(name => !_providers.ContainsKey(name)))
            {
                // Interest may have changed while another provider was being
                // initialized. Do not start an SDK for a stale request.
                if (!RequestedProviders().Contains(providerName) || !_config.EnabledIntegrations.Contains(providerName)) continue;
                if (!_providerFactories.TryGetValue(providerName, out var factory)) continue;
                if (providerName == "Msi" && !Program.IsRunningAsAdmin())
                {
                    Console.WriteLine("[Server] -> Skipping Msi: Not running as admin.");
                    continue;
                }
                IRGBDeviceProvider? provider = null;
                try
                {
                    provider = factory();
                    Console.WriteLine($"[Server] Initializing requested {providerName}...");
                    if (!provider.Initialize(throwExceptions: true))
                        throw new InvalidOperationException("Provider initialization failed.");
                    _providers[providerName] = provider;
                }
                catch (Exception error)
                {
                    Console.WriteLine($"[Server] -> WARNING: Initializing {providerName} failed: {error.Message}");
                    try { provider?.Dispose(); } catch { }
                }
            }

            var desired = new HashSet<IRGBDevice>(_providers.Values.SelectMany(provider => provider.Devices)
                .Where(d =>
                    !_config.DisabledDevices.Contains(
                        $"{d.DeviceInfo.Manufacturer}|{d.DeviceInfo.Model}"
                    )
                ), ReferenceEqualityComparer.Instance);
            var current = new HashSet<IRGBDevice>(_surface.Devices, ReferenceEqualityComparer.Instance);
            var removed = current.Where(device => !desired.Contains(device)).ToArray();
            var added = desired.Where(device => !current.Contains(device)).ToArray();
            if (removed.Length > 0) _surface.Detach(removed);
            if (added.Length > 0) _surface.Attach(added);

            Console.WriteLine($"[RGB.NET] Final device count: {_surface.Devices.Count()}");
            return BuildIdMaps();
        }
    }

    private int AllocateLegacyLedId()
    {
        if (_nextLegacyLedId > int.MaxValue)
            throw new InvalidOperationException("Legacy LED ID space exhausted; restart MineLights Server.");
        return (int)_nextLegacyLedId++;
    }

    private bool BuildIdMaps()
    {
        var devices = new HashSet<IRGBDevice>(_surface.Devices, ReferenceEqualityComparer.Instance);
        var leds = new HashSet<Led>(devices.SelectMany(device => device), ReferenceEqualityComparer.Instance);
        long newLedCount = leds.LongCount(led => !_legacyIdsByLed.ContainsKey(led));
        if (_nextLegacyLedId + newLedCount > (long)int.MaxValue + 1)
            throw new InvalidOperationException("Legacy LED ID space exhausted; restart MineLights Server.");
        bool changed = !_knownSurfaceDevices.SetEquals(devices) || !new HashSet<Led>(_legacyIdsByLed.Keys, ReferenceEqualityComparer.Instance).SetEquals(leds);
        _knownSurfaceDevices = devices;
        foreach (var old in _legacyIdsByLed.Keys.Where(led => !leds.Contains(led)).ToArray())
        {
            _legacyIdsByLed.Remove(old);
            _originalColorsByLed.Remove(old);
        }
        _ledIdMap.Clear();
        foreach (var device in _surface.Devices)
        {
            foreach (var led in device)
            {
                if (!_legacyIdsByLed.TryGetValue(led, out var id))
                {
                    id = AllocateLegacyLedId();
                    _legacyIdsByLed.Add(led, id);
                    // RGB.NET blends assignments by alpha. Capture an opaque
                    // RGB baseline so restoring a transparent initial color
                    // actually replaces a previously applied effect.
                    var color = led.Color;
                    _originalColorsByLed[led] = new RGB.NET.Core.Color(color.R, color.G, color.B);
                }
                _ledIdMap.Add(id, led);
            }
        }
        Console.WriteLine($"[Server] Mapped {_ledIdMap.Count} total LEDs across all devices.");
        RefreshV2DeviceMaps();
        return changed;
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
                    bool changed;
                    lock (_deviceLock)
                        changed = BuildIdMaps();
                    if (changed) _v2Protocol?.PublishDeviceListChanged();
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
                        ["provider"] = device.Provider ?? InferProviderName(device.Manufacturer),
                        ["legacy_device_key"] = $"{device.Manufacturer}|{device.Model}",
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
                    ["provider"] = GetProviderName(device),
                    ["legacy_device_key"] = $"{device.DeviceInfo.Manufacturer}|{device.DeviceInfo.Model}",
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
            _v2EmulatedLedMap.Clear();
            _v2DefaultColors.Clear();
            _v2DeviceByGlobalLed.Clear();
            _providerByDevice.Clear();
            foreach (var entry in _providers)
                foreach (var device in entry.Value.Devices)
                    _providerByDevice[device] = entry.Key;
            if (_isEmulationMode && _emulatedDevices != null && _emulatedLedMap != null)
            {
                var ids = ResolveEmulatedDeviceIds(_emulatedDevices);
                foreach (var device in _emulatedDevices)
                {
                    var deviceId = ids[device];
                    var emulatedMap = new Dictionary<uint, EmulatedLed>();
                    var defaults = new Dictionary<uint, RGB.NET.Core.Color>();
                    for (uint index = 0; index < device.Leds.Count; index++)
                    {
                        var emulated = device.Leds[(int)index];
                        var globalId = _emulatedIdsByLed![emulated];
                        emulatedMap[index] = emulated;
                        if (_emulatedDefaultColors != null && _emulatedDefaultColors.TryGetValue(globalId, out var original))
                            defaults[index] = original;
                        _v2DeviceByGlobalLed[globalId] = deviceId;
                    }
                    _v2EmulatedLedMap[deviceId] = emulatedMap;
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
                    if (_originalColorsByLed.TryGetValue(led, out var original)) defaults[index] = original;
                    if (_legacyIdsByLed.TryGetValue(led, out var globalId)) _v2DeviceByGlobalLed[globalId] = deviceId;
                    index++;
                }
                _v2LedMap[deviceId] = ledMap;
                _v2DefaultColors[deviceId] = defaults;
            }
        }
    }

    private string? GetProviderName(IRGBDevice device)
    {
        lock (_v2MapLock)
            return _providerByDevice.TryGetValue(device, out var provider) ? provider : null;
    }

    // Older emulation snapshots predate the provider field. Infer only known
    // manufacturers; real devices use their actual RGB.NET provider instead.
    private static string? InferProviderName(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        var name = manufacturer.Trim();
        if (name.Equals("RGB.NET", StringComparison.OrdinalIgnoreCase)) return "PicoPi";
        if (name.Equals("ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ASUSTeK COMPUTER INC", StringComparison.OrdinalIgnoreCase)) return "Asus";
        if (name.Equals("Micro-Star International Co., Ltd.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Micro-Star International Co., Ltd", StringComparison.OrdinalIgnoreCase)) return "Msi";
        return DefaultProviders.FirstOrDefault(provider => provider.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string>? ReadLegacyFilter(JObject config, string property)
    {
        var token = config[property];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JArray values || values.Any(value => value.Type != JTokenType.String))
            throw new JsonException($"Invalid {property} in legacy client config.");
        return new HashSet<string>(values.Select(value => value.Value<string>()!), StringComparer.Ordinal);
    }

    private static bool LegacyDeviceAllowed(HashSet<string>? enabled, HashSet<string>? disabled,
        string? provider, string manufacturer, string model)
        => (enabled == null || (provider != null && enabled.Contains(provider)))
            && (disabled == null || !disabled.Contains($"{manufacturer}|{model}"));

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
                if (deviceId == null || target["colors"] is not JArray colors)
                    continue;
                _v2LedMap.TryGetValue(deviceId, out var ledMap);
                _v2EmulatedLedMap.TryGetValue(deviceId, out var emulatedMap);
                foreach (var color in colors.OfType<JObject>())
                {
                    if (!uint.TryParse(color["element_id"]?.ToString(), out var elementId))
                        continue;
                    var red = color.Value<int>("r");
                    var green = color.Value<int>("g");
                    var blue = color.Value<int>("b");
                    if (_isEmulationMode && emulatedMap != null && emulatedMap.TryGetValue(elementId, out var emulated))
                    {
                        emulated.R = red / 255f;
                        emulated.G = green / 255f;
                        emulated.B = blue / 255f;
                    }
                    else if (!_isEmulationMode && ledMap != null && ledMap.TryGetValue(elementId, out var led))
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
                if (!_v2DefaultColors.TryGetValue(deviceId, out var defaults))
                    continue;
                if (_isEmulationMode && _v2EmulatedLedMap.TryGetValue(deviceId, out var emulatedLeds))
                {
                    foreach (var pair in emulatedLeds)
                    {
                        if (!defaults.TryGetValue(pair.Key, out var color)) continue;
                        pair.Value.A = color.A;
                        pair.Value.R = color.R;
                        pair.Value.G = color.G;
                        pair.Value.B = color.B;
                    }
                }
                else if (!_isEmulationMode && _v2LedMap.TryGetValue(deviceId, out var leds))
                    foreach (var pair in leds)
                        if (defaults.TryGetValue(pair.Key, out var color)) pair.Value.Color = color;
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
                    var clientConfig = JObject.Parse(clientConfigJson);
                    var enabled = ReadLegacyFilter(clientConfig, "enabled_integrations");
                    var disabled = ReadLegacyFilter(clientConfig, "disabled_devices");
                    if (!_config.AllowLegacyLocalClients)
                    {
                        SetLegacyProviderInterest(enabled); // Record only; never initialize before owner approval.
                        NotifyLegacyClientAttempt();
                        continue;
                    }

                    // Filter only this legacy response. A V1 client must not
                    // reconfigure the shared server or other clients' devices.
                    if (!SetLegacyProviderInterest(enabled)) continue;

                    lock (_deviceLock)
                    {
                        // Consent can be revoked while a requested provider is
                        // initializing; do not return a stale approved layout.
                        if (!_config.AllowLegacyLocalClients) continue;
                        JArray devicesArray;

                        if (_isEmulationMode && _emulatedDevices != null)
                        {
                            devicesArray = new JArray();

                            foreach (var dev in _emulatedDevices)
                            {
                                if (!LegacyDeviceAllowed(enabled, disabled, dev.Provider ?? InferProviderName(dev.Manufacturer), dev.Manufacturer, dev.Model))
                                    continue;
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
                                if (!LegacyDeviceAllowed(enabled, disabled, GetProviderName(device), device.DeviceInfo.Manufacturer, device.DeviceInfo.Model))
                                    continue;
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

    private void UdpServerLoop(UdpClient udpClient, CancellationToken ct)
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (_isRunning && ReferenceEquals(_udpClient, udpClient) && !ct.IsCancellationRequested)
            {
                if (udpClient.Available == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                byte[] recvBuffer = udpClient.Receive(ref from);
                string jsonString = Encoding.UTF8.GetString(recvBuffer);

                JArray? colors = null;
                try
                {
                    colors = JObject.Parse(jsonString)["led_colors"] as JArray;
                }
                catch (JsonException) { }

                if (colors != null)
                {
                    var updates = new List<(int Id, byte R, byte G, byte B)>();
                    bool valid = true;
                    foreach (var token in colors)
                    {
                        if (token is not JObject item
                            || !TryLegacyInteger(item["id"], int.MaxValue, out var id)
                            || !TryLegacyInteger(item["r"], byte.MaxValue, out var red)
                            || !TryLegacyInteger(item["g"], byte.MaxValue, out var green)
                            || !TryLegacyInteger(item["b"], byte.MaxValue, out var blue))
                        {
                            valid = false;
                            break;
                        }
                        updates.Add((id, (byte)red, (byte)green, (byte)blue));
                    }
                    if (!valid) continue;
                    void ApplyFrame()
                    {
                        lock (_deviceLock)
                        {
                            // Reject datagrams queued before legacy access was
                            // revoked or before this listener was replaced.
                            if (!_isRunning || !_config.AllowLegacyLocalClients || !ReferenceEquals(_udpClient, udpClient)) return;
                            var surfaceChanged = false;
                            foreach (var item in updates)
                            {
                                int id = item.Id;
                                if (IsV2DeviceLeasedForLegacyLed(id)) continue;
                                if (_isEmulationMode)
                                {
                                    if (_emulatedLedMap != null && _emulatedLedMap.TryGetValue(id, out var emu))
                                    {
                                        emu.R = item.R / 255f;
                                        emu.G = item.G / 255f;
                                        emu.B = item.B / 255f;
                                    }
                                }
                                else if (_ledIdMap.TryGetValue(id, out Led led))
                                {
                                    led.Color = new RGB.NET.Core.Color(item.R, item.G, item.B);
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
        catch (NullReferenceException) when (!_isRunning || !ReferenceEquals(_udpClient, udpClient)) { }
    }

    private static bool TryLegacyInteger(JToken? token, int maximum, out int value)
    {
        value = 0;
        return token?.Type == JTokenType.Integer
            && int.TryParse(token.ToString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            && value >= 0 && value <= maximum;
    }

    private bool IsV2DeviceLeasedForLegacyLed(int globalLedId)
    {
        lock (_v2MapLock)
        {
            return _v2DeviceByGlobalLed.TryGetValue(globalLedId, out var deviceId)
                && _v2Protocol?.IsDeviceLeased(deviceId) == true;
        }
    }

    private void DiscoveryBroadcastLoop(UdpClient client, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, 63214);
        byte[] message = Encoding.UTF8.GetBytes("MINELIGHTS_PROXY_HELLO");

        try
        {
            while (_isRunning && ReferenceEquals(_discoveryClient, client) && !ct.IsCancellationRequested)
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
        catch (NullReferenceException) when (!_isRunning || !ReferenceEquals(_discoveryClient, client)) { }
    }
}
