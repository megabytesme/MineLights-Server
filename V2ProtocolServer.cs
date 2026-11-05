using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// MineLights Protocol V2 transport, authentication, pairing, and session manager.
/// Device enumeration and frame application remain owned by LightingServer.
/// </summary>
public sealed class V2ProtocolServer : IDisposable
{
    private const ushort ProtocolMajor = 2;
    private const ushort ProtocolMinMinor = 0;
    private const ushort ProtocolMinor = 0;
    private const int HeaderLength = 16;
    private const int MaxPayloadLength = 8 * 1024 * 1024;
    private const int MaxControlPayloadLength = 1024 * 1024;
    private const int MaxConcurrentConnections = 16;
    private const int MaxRemotePreAuthenticationConnections = 4;
    private const string ServerVersion = "2.0";

    private const ushort ClientHelloType = 0x0001;
    private const ushort ServerHelloType = 0x0002;
    private const ushort ClientAuthType = 0x0003;
    private const ushort AuthResultType = 0x0004;
    private const ushort DeviceListType = 0x0010;
    private const ushort DeviceChangedType = 0x0011;
    private const ushort LightingFrameType = 0x0020;
    private const ushort PingType = 0x0030;
    private const ushort PongType = 0x0031;
    private const ushort RequestType = 0x0040;
    private const ushort ResponseType = 0x0041;
    private const ushort ErrorType = 0x00FE;
    private const ushort GoodbyeType = 0x00FF;

    private readonly Func<JArray> _getDevices;
    private readonly Action<JObject> _applyFrame;
    private readonly Action<IReadOnlyCollection<string>> _restoreDevices;
    private readonly object _deviceCacheLock = new();
    private JArray _deviceSnapshot = new();
    private Dictionary<string, HashSet<uint>> _deviceElements = new(StringComparer.Ordinal);
    private readonly string _dataDirectory;
    private readonly string _serverId;
    private readonly byte[] _localEnrollmentKey;
    private readonly X509Certificate2 _certificate;
    private readonly string _certificateFingerprint;
    private readonly object _storageLock = new();
    private readonly object _leaseLock = new();
    private readonly object _frameLock = new();
    private readonly Dictionary<string, ClientCredential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PairingCredential> _pairingCodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expiredPairingIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredPairingOrder = new();
    private readonly Dictionary<string, Lease> _leasesByDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly SemaphoreSlim _remotePreAuthenticationSlots = new(MaxRemotePreAuthenticationConnections, MaxRemotePreAuthenticationConnections);
    private readonly ConditionalWeakTable<Stream, SemaphoreSlim> _streamWriteLocks = new();
    private readonly ConditionalWeakTable<Stream, NegotiatedProtocolVersion> _streamProtocolVersions = new();
    private readonly ConcurrentDictionary<string, RateLimitBucket> _authRateLimits = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _listenerLock = new();
    private List<TcpListener> _listeners = new();
    private List<Task> _acceptTasks = new();
    private CancellationTokenSource? _acceptCancellation;
    private Task? _leaseCleanupTask;
    private bool _remoteEnabled;
    private List<IPAddress> _remoteAddresses = new();
    private int _requestedPort = 63215;
    private int _actualPort;
    private long _deviceRevision = 1;
    private long _lastConnectionLimitNoticeTicks;
    private long _lastRemotePreAuthenticationLimitNoticeTicks;
    private long _sessionOrder;
    private long _nextClientOrder;
    private readonly Dictionary<string, long> _clientOrders = new(StringComparer.Ordinal);
    private bool _stopping;
    private DateTimeOffset _shutdownWhenIdleUntil;

    public string ServerId => _serverId;
    public string CertificateFingerprint => _certificateFingerprint;
    public int Port => _actualPort;
    public bool RemoteEnabled => _remoteEnabled;
    public IReadOnlyList<IPAddress> RemoteAddresses => _remoteAddresses;
    public bool IsDeviceLeased(string deviceId)
    {
        lock (_leaseLock)
        {
            return _leasesByDevice.TryGetValue(deviceId, out var lease)
                && lease.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }
    }

    public void ApplyLegacyFrame(Action applyFrame)
    {
        lock (_frameLock) applyFrame();
    }

    public void PublishDeviceListChanged()
    {
        RefreshDeviceCache();
        Interlocked.Increment(ref _deviceRevision);
        foreach (var session in _activeSessions.Values.Where(session => session.Ready))
            _ = SendDeviceChangedAsync(session);
    }

    public V2ProtocolServer(
        Func<JArray> getDevices,
        Action<JObject> applyFrame,
        Action<IReadOnlyCollection<string>> restoreDevices,
        string? dataDirectory = null)
    {
        _getDevices = getDevices;
        _applyFrame = applyFrame;
        _restoreDevices = restoreDevices;
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MineLights",
            "v2");
        Directory.CreateDirectory(_dataDirectory);
        ProtectDirectory(_dataDirectory);

        _serverId = LoadOrCreateServerId();
        _localEnrollmentKey = LoadOrCreateKey(Path.Combine(_dataDirectory, "local-enrollment.key"));
        _certificate = LoadOrCreateCertificate();
        _certificateFingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
        LoadCredentialStore();
        RefreshDeviceCache();
    }

    public void Start()
    {
        StartListeners(remoteAddresses: Array.Empty<IPAddress>(), requestedPort: 0);
        _leaseCleanupTask = Task.Run(() => LeaseCleanupLoopAsync(_shutdown.Token));
    }

    public void ConfigureRemoteAccess(IEnumerable<IPAddress> addresses, int requestedPort = 63215)
    {
        var requestedAddresses = addresses.ToArray();
        if (requestedAddresses.Any(address => address.IsIPv6LinkLocal))
            throw new ArgumentException("IPv6 link-local addresses cannot be advertised to remote clients. Select an IPv4 or globally reachable IPv6 address.", nameof(addresses));
        var validAddresses = requestedAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6)
            .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            .Distinct()
            .ToList();
        if (validAddresses.Count == 0)
            throw new ArgumentException("Select at least one non-loopback network address.", nameof(addresses));
        if (requestedPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(requestedPort));

        StartListeners(validAddresses, requestedPort == 0 ? 63215 : requestedPort);
    }

    public void DisableRemoteAccess()
    {
        StartListeners(Array.Empty<IPAddress>(), 0);
    }

    public string ExportPairingFile(string path, IPAddress? selectedRemoteAddress = null)
    {
        if (!_remoteEnabled)
            throw new InvalidOperationException("Enable remote access before creating a pairing file.");
        IPAddress address;
        if (selectedRemoteAddress != null)
        {
            if (!_remoteAddresses.Contains(selectedRemoteAddress))
                throw new ArgumentException("Choose one of the enabled remote listener addresses.", nameof(selectedRemoteAddress));
            address = selectedRemoteAddress;
        }
        else if (_remoteAddresses.Count == 1)
        {
            address = _remoteAddresses[0];
        }
        else
        {
            throw new InvalidOperationException("Choose a remote listener address before exporting a pairing file.");
        }

        var pairingId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var code = RandomNumberGenerator.GetBytes(32);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        lock (_storageLock)
        {
            foreach (var expiredId in _pairingCodes.Where(pair => pair.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
            {
                RememberExpiredPairing(expiredId);
                _pairingCodes.Remove(expiredId);
            }
            if (_pairingCodes.Count >= 32)
                throw new InvalidOperationException("There are already 32 active pairing files. Wait for an existing file to expire or pair its client first.");
            _pairingCodes[pairingId] = new PairingCredential(code, expiresAt);
        }

        var pairingFile = new JObject
        {
            ["schema_version"] = 1,
            ["server_id"] = _serverId,
            ["host"] = address.ToString(),
            ["port"] = _actualPort,
            ["tls_certificate_sha256"] = _certificateFingerprint,
            ["pairing_id"] = pairingId,
            ["pairing_code"] = Base64UrlEncode(code),
            ["expires_at_utc"] = expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
        };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        WriteProtectedFile(fullPath, new UTF8Encoding(false).GetBytes(pairingFile.ToString(Formatting.Indented)));
        return fullPath;
    }

    public void RevokeClient(string clientId)
    {
        lock (_storageLock)
        {
            _credentials.Remove(clientId);
            SaveCredentialStore();
        }
        DisconnectClientSessions(clientId);
    }

    public IReadOnlyList<JObject> ListClients()
    {
        lock (_storageLock)
        {
            return _credentials.Values
                .Select(credential => new JObject
                {
                    ["client_id"] = credential.ClientId,
                    ["app_id"] = credential.AppId,
                    ["app_version"] = credential.AppVersion,
                    ["display_name"] = credential.DisplayName,
                    ["scopes"] = new JArray(credential.Scopes),
                    ["priority"] = credential.Priority
                })
                .ToList();
        }
    }

    public void SetClientPriority(string clientId, int priority)
    {
        if (priority is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_storageLock)
        {
            if (!_credentials.TryGetValue(clientId, out var current))
                throw new ArgumentException("Unknown client", nameof(clientId));
            _credentials[clientId] = current with { Priority = priority };
            foreach (var session in _activeSessions.Values.Where(s => s.ClientId == clientId))
                session.Priority = priority;
            SaveCredentialStore();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var session in _activeSessions.Values)
            CloseSession(session);
        StopListeners();
        RemoveEndpointDocumentIfOwned();
        try { _leaseCleanupTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _certificate.Dispose(); } catch { }
        _shutdown.Dispose();
    }

    private void StartListeners(IEnumerable<IPAddress> remoteAddresses, int requestedPort)
    {
        lock (_listenerLock)
        {
            CancelRemoteSessions();
            StopListenersLocked();
            lock (_storageLock)
            {
                foreach (var pairingId in _pairingCodes.Keys)
                    RememberExpiredPairing(pairingId);
                _pairingCodes.Clear();
            }
            _remoteAddresses = remoteAddresses.ToList();
            _remoteEnabled = _remoteAddresses.Count > 0;
            _requestedPort = requestedPort;

            List<TcpListener>? listeners = TryStartListenerSet(_remoteAddresses, requestedPort, out var port);
            if (listeners == null && requestedPort != 0)
                listeners = TryStartListenerSet(_remoteAddresses, 0, out port);
            if (listeners == null)
            {
                _remoteEnabled = false;
                _remoteAddresses.Clear();
                listeners = TryStartListenerSet(Array.Empty<IPAddress>(), 0, out port);
                if (listeners == null)
                    throw new IOException("Could not bind the V2 loopback endpoint.");
                Console.WriteLine("[V2] Requested remote listener could not bind; the authenticated loopback endpoint remains available.");
            }

            _listeners = listeners;
            _actualPort = port;
            var acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _acceptCancellation = acceptCancellation;
            var acceptToken = acceptCancellation.Token;
            _acceptTasks = listeners.Select(listener => Task.Run(() => AcceptLoopAsync(listener, acceptToken))).ToList();
            WriteEndpointDocument();
            Console.WriteLine($"[V2] Listening on port {_actualPort}; remote access {(_remoteEnabled ? "enabled" : "disabled")}.");
        }
    }

    private List<TcpListener>? TryStartListenerSet(IReadOnlyList<IPAddress> remoteAddresses, int requestedPort, out int actualPort)
    {
        actualPort = 0;
        var listeners = new List<TcpListener>();
        try
        {
            var loopback = new TcpListener(IPAddress.Loopback, requestedPort);
            loopback.Start(32);
            actualPort = ((IPEndPoint)loopback.LocalEndpoint).Port;
            listeners.Add(loopback);

            foreach (var address in remoteAddresses)
            {
                var listener = new TcpListener(address, actualPort);
                listener.Start(32);
                listeners.Add(listener);
            }
            return listeners;
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            foreach (var listener in listeners)
            {
                try { listener.Stop(); } catch { }
            }
            Console.WriteLine($"[V2] Listener bind failed: {error.Message}");
            return null;
        }
    }

    private void StopListeners()
    {
        lock (_listenerLock)
            StopListenersLocked();
    }

    private void StopListenersLocked()
    {
        _acceptCancellation?.Cancel();
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
        try { Task.WaitAll(_acceptTasks.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        _listeners.Clear();
        _acceptTasks.Clear();
        _acceptCancellation?.Dispose();
        _acceptCancellation = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var localAddress = (listener.LocalEndpoint as IPEndPoint)?.Address;
        var preAuthenticationSlots = localAddress != null && !IPAddress.IsLoopback(localAddress)
            ? _remotePreAuthenticationSlots
            : null;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (SocketException error)
            {
                Console.WriteLine($"[V2] Accept failed: {error.Message}");
                continue;
            }

            if (!_connectionSlots.Wait(0))
            {
                client.Dispose();
                LogConnectionLimitRejection();
                continue;
            }
            if (preAuthenticationSlots != null && !preAuthenticationSlots.Wait(0))
            {
                client.Dispose();
                _connectionSlots.Release();
                LogRemotePreAuthenticationLimitRejection();
                continue;
            }
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                }
                catch (SocketException) { /* Keep the operating system defaults where tuning is unavailable. */ }
                _ = Task.Run(() => HandleAdmittedClientAsync(client, cancellationToken, preAuthenticationSlots));
            }
            catch
            {
                client.Dispose();
                preAuthenticationSlots?.Release();
                _connectionSlots.Release();
            }
        }
    }

    private async Task HandleAdmittedClientAsync(TcpClient client, CancellationToken listenerCancellation, SemaphoreSlim? preAuthenticationSlots)
    {
        Action? releasePreAuthenticationSlot = null;
        if (preAuthenticationSlots != null)
        {
            var preAuthenticationSlotHeld = 1;
            releasePreAuthenticationSlot = () =>
            {
                if (Interlocked.Exchange(ref preAuthenticationSlotHeld, 0) == 1)
                    preAuthenticationSlots!.Release();
            };
        }
        try
        {
            await HandleClientAsync(client, listenerCancellation, releasePreAuthenticationSlot).ConfigureAwait(false);
        }
        finally
        {
            releasePreAuthenticationSlot?.Invoke();
            _connectionSlots.Release();
        }
    }

    private void LogConnectionLimitRejection()
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastConnectionLimitNoticeTicks);
        if (now - previous < TimeSpan.FromSeconds(10).Ticks
            || Interlocked.CompareExchange(ref _lastConnectionLimitNoticeTicks, now, previous) != previous)
            return;
        Console.WriteLine($"[V2] Rejected connections after reaching the global limit of {MaxConcurrentConnections} concurrent local and remote clients.");
    }

    private void LogRemotePreAuthenticationLimitRejection()
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastRemotePreAuthenticationLimitNoticeTicks);
        if (now - previous < TimeSpan.FromSeconds(10).Ticks
            || Interlocked.CompareExchange(ref _lastRemotePreAuthenticationLimitNoticeTicks, now, previous) != previous)
            return;
        Console.WriteLine($"[V2] Rejected remote connections after reaching the pre-authentication limit of {MaxRemotePreAuthenticationConnections} concurrent clients.");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellation, Action? releasePreAuthenticationSlot)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellationToken = timeout.Token;
            var sessionId = Guid.NewGuid().ToString("N");
            var clientId = string.Empty;
            var localAddress = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
            var isRemoteSession = localAddress != null && !IPAddress.IsLoopback(localAddress);
            try
            {
                using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, cancellationToken).ConfigureAwait(false);

                var hello = await ReadMessageAsync(ssl, cancellationToken).ConfigureAwait(false);
                if (hello == null || hello.Type != ClientHelloType)
                {
                    await TrySendErrorAsync(ssl, "EXPECTED_CLIENT_HELLO", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (hello.Major != ProtocolMajor)
                {
                    await TrySendErrorAsync(ssl, "VERSION_UNSUPPORTED", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var payload = hello.Payload;
                clientId = NormalizeUuid(payload.Value<string>("client_id"));
                var appId = NormalizeAppId(payload.Value<string>("app_id"));
                var appVersion = SafeDisplay(payload.Value<string>("app_version"), 64);
                var minMinor = payload.Value<int?>("min_minor") ?? 0;
                var maxMinor = payload.Value<int?>("max_minor") ?? 0;
                if (clientId == string.Empty
                    || appId == string.Empty
                    || minMinor < 0
                    || maxMinor < 0
                    || minMinor > ushort.MaxValue
                    || maxMinor > ushort.MaxValue
                    || minMinor > maxMinor
                    || minMinor > ProtocolMinor
                    || maxMinor < ProtocolMinMinor)
                {
                    await TrySendErrorAsync(ssl, "VERSION_UNSUPPORTED", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var selectedMinor = (ushort)Math.Min(maxMinor, ProtocolMinor);
                if (selectedMinor < Math.Max(minMinor, ProtocolMinMinor))
                {
                    await TrySendErrorAsync(ssl, "VERSION_UNSUPPORTED", cancellationToken).ConfigureAwait(false);
                    return;
                }
                _streamProtocolVersions.Add(ssl, new NegotiatedProtocolVersion(selectedMinor));

                var serverNonce = RandomNumberGenerator.GetBytes(32);
                await WriteMessageAsync(ssl, ServerHelloType, new JObject
                {
                    ["server_id"] = _serverId,
                    ["server_name"] = Environment.MachineName,
                    ["server_version"] = ServerVersion,
                    ["selected_minor"] = selectedMinor,
                    ["nonce"] = Base64UrlEncode(serverNonce),
                    ["capabilities"] = new JArray("device_leases", "generic_devices", "json_frames")
                }, cancellationToken).ConfigureAwait(false);

                var auth = await ReadMessageAsync(ssl, cancellationToken).ConfigureAwait(false);
                if (auth == null || auth.Type != ClientAuthType)
                {
                    await TrySendErrorAsync(ssl, "EXPECTED_CLIENT_AUTH", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var authType = auth.Payload.Value<string>("credential_type") ?? string.Empty;
                var credentialId = auth.Payload.Value<string>("credential_id") ?? string.Empty;
                if (!TryDecodeBase64Url(auth.Payload.Value<string>("client_nonce"), 32, out var clientNonce)
                    || !TryDecodeBase64Url(auth.Payload.Value<string>("proof"), 32, out var clientProof))
                {
                    await TrySendAuthResultAsync(ssl, false, Array.Empty<string>(), "BAD_PROOF", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var isLoopback = remoteAddress != null && IPAddress.IsLoopback(remoteAddress);
                var credential = ResolveCredential(authType, credentialId, clientId, isLoopback, out var credentialError);
                if (credential == null)
                {
                    await TrySendAuthResultAsync(ssl, false, Array.Empty<string>(), credentialError, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var expectedProof = MakeAuthProof(credential.Secret, authType, credentialId, _serverId, clientId, serverNonce, clientNonce, ProtocolMajor, selectedMinor);
                if (!CryptographicOperations.FixedTimeEquals(expectedProof, clientProof))
                {
                    RegisterAuthFailure(remoteAddress);
                    await TrySendAuthResultAsync(ssl, false, Array.Empty<string>(), authType == "pairing_code" ? "PAIRING_REQUIRED" : "BAD_PROOF", cancellationToken).ConfigureAwait(false);
                    return;
                }
                ResetAuthFailures(remoteAddress);
                releasePreAuthenticationSlot?.Invoke();

                if (credential.OneTime)
                {
                    if (authType == "pairing_code" && !TryConsumePairingCode(credentialId, out var pairingError))
                    {
                        await TrySendAuthResultAsync(ssl, false, Array.Empty<string>(), pairingError, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await WriteMessageAsync(ssl, AuthResultType, new JObject
                    {
                        ["ok"] = true,
                        ["scopes"] = new JArray("client.enroll")
                    }, cancellationToken).ConfigureAwait(false);
                    await RegisterClientAsync(ssl, credential, clientId, appId, appVersion, cancellationToken).ConfigureAwait(false);
                    return;
                }

                clientId = credential.ClientId;
                var scopes = credential.Scopes;
                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var sessionToken = sessionCancellation.Token;
                var activeSession = new ActiveSession(clientId, sessionCancellation, isRemoteSession, ssl);
                if (!TryTrackSession(sessionId, activeSession, credential, isRemoteSession, serverCancellation))
                {
                    var code = isRemoteSession && serverCancellation.IsCancellationRequested ? "PAIRING_REQUIRED" : "UNKNOWN_CLIENT";
                    await TrySendAuthResultAsync(ssl, false, Array.Empty<string>(), code, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WriteMessageAsync(ssl, AuthResultType, new JObject
                {
                    ["ok"] = true,
                    ["scopes"] = new JArray(scopes)
                }, sessionToken).ConfigureAwait(false);
                activeSession.DeviceRevision = await SendDeviceListAsync(ssl, sessionToken).ConfigureAwait(false);
                activeSession.Ready = true;
                if (activeSession.DeviceRevision < Interlocked.Read(ref _deviceRevision))
                    _ = SendDeviceChangedAsync(activeSession);
                await SessionLoopAsync(ssl, sessionId, clientId, scopes, sessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (AuthenticationException error)
            {
                Console.WriteLine($"[V2] TLS negotiation rejected: {error.Message}");
            }
            catch (JsonException error)
            {
                Console.WriteLine($"[V2] Malformed JSON from a client: {error.Message}");
            }
            catch (Exception error)
            {
                Console.WriteLine($"[V2] Client session ended: {error.Message}");
            }
            finally
            {
                ReleaseSessionLeases(sessionId);
                bool shutdownWhenIdle = false;
                lock (_storageLock)
                {
                    _activeSessions.TryRemove(sessionId, out _);
                    if (_shutdownWhenIdleUntil > DateTimeOffset.UtcNow && _activeSessions.IsEmpty && !_stopping)
                    {
                        _stopping = true;
                        shutdownWhenIdle = true;
                    }
                }
                if (shutdownWhenIdle) Program.RequestShutdown();
            }
        }
    }

    private ClientCredential? ResolveCredential(string authType, string credentialId, string clientId, bool isLoopback, out string failureCode)
    {
        failureCode = authType == "client_secret" ? "UNKNOWN_CLIENT" : "PAIRING_REQUIRED";
        lock (_storageLock)
        {
            if (authType == "client_secret")
            {
                if (!Guid.TryParse(clientId, out _) || !string.Equals(credentialId, clientId, StringComparison.OrdinalIgnoreCase))
                    return null;
                return _credentials.TryGetValue(clientId, out var record)
                    ? record
                    : null;
            }

            if (authType == "local_enrollment")
            {
                if (!isLoopback || credentialId != "local")
                    return null;
                return new ClientCredential(clientId, string.Empty, string.Empty, string.Empty, _localEnrollmentKey, new[] { "client.enroll" }, OneTime: true);
            }

            if (authType == "pairing_code")
            {
                if (!_remoteEnabled || isLoopback || !Guid.TryParse(credentialId, out _))
                    return null;
                if (!_pairingCodes.TryGetValue(credentialId, out var pairing))
                {
                    if (_expiredPairingIds.Contains(credentialId))
                        failureCode = "PAIRING_EXPIRED";
                    return null;
                }
                if (pairing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    RememberExpiredPairing(credentialId);
                    _pairingCodes.Remove(credentialId);
                    failureCode = "PAIRING_EXPIRED";
                    return null;
                }
                if (pairing.Attempts >= 5)
                    return null;
                pairing.Attempts++;
                return new ClientCredential(clientId, string.Empty, string.Empty, string.Empty, pairing.Code, new[] { "client.enroll" }, OneTime: true);
            }

            return null;
        }
    }

    private bool TryTrackSession(string sessionId, ActiveSession session, ClientCredential credential, bool isRemote, CancellationToken listenerCancellation)
    {
        if (isRemote)
        {
            lock (_listenerLock)
            {
                if (listenerCancellation.IsCancellationRequested || !_remoteEnabled)
                    return false;
                return TryTrackSessionUnderCredentialLock(sessionId, session, credential);
            }
        }
        return TryTrackSessionUnderCredentialLock(sessionId, session, credential);
    }

    private bool TryTrackSessionUnderCredentialLock(string sessionId, ActiveSession session, ClientCredential credential)
    {
        lock (_storageLock)
        {
            if (_stopping || !_credentials.TryGetValue(credential.ClientId, out var currentCredential) || !ReferenceEquals(currentCredential, credential))
                return false;
            session.Priority = credential.Priority;
            if (!_clientOrders.TryGetValue(session.ClientId, out var clientOrder))
                _clientOrders[session.ClientId] = clientOrder = ++_nextClientOrder;
            session.ClientOrder = clientOrder;
            session.Order = ++_sessionOrder;
            _activeSessions[sessionId] = session;
            return true;
        }
    }

    private bool TryConsumePairingCode(string pairingId, out string errorCode)
    {
        lock (_storageLock)
        {
            if (!_pairingCodes.TryGetValue(pairingId, out var pairing))
            {
                errorCode = _expiredPairingIds.Contains(pairingId) ? "PAIRING_EXPIRED" : "PAIRING_REQUIRED";
                return false;
            }
            if (pairing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _pairingCodes.Remove(pairingId);
                RememberExpiredPairing(pairingId);
                errorCode = "PAIRING_EXPIRED";
                return false;
            }
            _pairingCodes.Remove(pairingId);
            errorCode = string.Empty;
            return true;
        }
    }

    private void RememberExpiredPairing(string pairingId)
    {
        if (!_expiredPairingIds.Add(pairingId))
            return;
        _expiredPairingOrder.Enqueue(pairingId);
        while (_expiredPairingOrder.Count > 256)
            _expiredPairingIds.Remove(_expiredPairingOrder.Dequeue());
    }

    private async Task<bool> RegisterClientAsync(Stream stream, ClientCredential enrollment, string clientId, string helloAppId, string helloAppVersion, CancellationToken cancellationToken)
    {
        var request = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request == null || request.Type != RequestType || request.Payload.Value<string>("operation") != "client.register")
        {
            await TrySendErrorAsync(stream, "EXPECTED_CLIENT_REGISTER", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var requestId = NormalizeRequestId(request.Payload["request_id"]);
        if (requestId == null)
        {
            await TrySendErrorAsync(stream, "INVALID_REQUEST_ID", cancellationToken).ConfigureAwait(false);
            return false;
        }
        var parameters = request.Payload["params"] as JObject ?? new JObject();
        var registeredClientId = NormalizeUuid(parameters.Value<string>("client_id"));
        var appId = NormalizeAppId(parameters.Value<string>("app_id"));
        var appVersion = SafeDisplay(parameters.Value<string>("app_version"), 64);
        var displayName = SafeDisplay(parameters.Value<string>("display_name"), 80);
        if (registeredClientId != clientId || appId != helloAppId || (helloAppVersion.Length > 0 && appVersion != helloAppVersion)
            || !TryDecodeBase64Url(parameters.Value<string>("client_secret"), 32, out var secret))
        {
            await WriteMessageAsync(stream, ResponseType, new JObject
            {
                ["request_id"] = requestId,
                ["ok"] = false,
                ["error_code"] = "INVALID_REGISTRATION"
            }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var requestedScopes = (parameters["requested_scopes"] as JArray ?? new JArray())
            .Select(token => token.Type == JTokenType.String ? token.Value<string>() : null)
            .OfType<string>()
            .Where(scope => scope is "devices.read" or "lighting.write")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!requestedScopes.Contains("devices.read", StringComparer.Ordinal))
            requestedScopes = requestedScopes.Append("devices.read").ToArray();
        var scopes = requestedScopes;

        lock (_storageLock)
        {
            _credentials[clientId] = new ClientCredential(clientId, appId, appVersion, displayName, secret, scopes, OneTime: false);
            SaveCredentialStore();
        }
        await WriteMessageAsync(stream, ResponseType, new JObject
        {
            ["request_id"] = requestId,
            ["ok"] = true,
            ["result"] = new JObject { ["granted_scopes"] = new JArray(scopes) }
        }, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[V2] Registered client {clientId} ({appId}); credentials omitted.");
        return true;
    }

    private async Task SessionLoopAsync(Stream stream, string sessionId, string clientId, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        uint? lastSequence = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (message == null || message.Type == GoodbyeType)
                return;

            if (message.Type == PingType)
            {
                await WriteMessageAsync(stream, PongType, new JObject { ["nonce"] = message.Payload.Value<string>("nonce") }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message.Type == RequestType)
            {
                await HandleRequestAsync(stream, sessionId, clientId, message.Payload, scopes, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message.Type == LightingFrameType)
            {
                if (!scopes.Contains("lighting.write", StringComparer.Ordinal))
                {
                    await TrySendErrorAsync(stream, "SCOPE_REQUIRED", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (message.Payload["sequence"] == null || !uint.TryParse(message.Payload["sequence"]!.ToString(), out var candidateSequence))
                {
                    await TrySendErrorAsync(stream, "INVALID_FRAME", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (lastSequence.HasValue && !IsSequenceNewer(candidateSequence, lastSequence.Value))
                {
                    await TrySendErrorAsync(stream, "STALE_FRAME", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                uint sequence;
                string errorCode;
                bool valid;
                lock (_frameLock)
                {
                    valid = ValidateAndRefreshLeases(sessionId, message.Payload, out sequence, out errorCode);
                    if (valid) _applyFrame(message.Payload);
                }
                if (!valid)
                {
                    await TrySendErrorAsync(stream, errorCode, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                lastSequence = sequence;
                continue;
            }

            await TrySendErrorAsync(stream, "MESSAGE_NOT_ALLOWED", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(Stream stream, string sessionId, string clientId, JObject payload, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        var requestId = NormalizeRequestId(payload["request_id"]);
        if (requestId == null)
        {
            await TrySendErrorAsync(stream, "INVALID_REQUEST_ID", cancellationToken).ConfigureAwait(false);
            return;
        }
        var operation = payload.Value<string>("operation") ?? string.Empty;
        var parameters = payload["params"] as JObject ?? new JObject();
        if (operation == "server.info")
        {
            await SendResponseSuccessAsync(stream, requestId, new JObject
            {
                ["launch_id"] = Program.ClientLaunchId == null ? JValue.CreateNull() : new JValue(Program.ClientLaunchId)
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation == "server.shutdown")
        {
            bool accepted;
            lock (_storageLock)
            {
                accepted = !_stopping && _activeSessions.ContainsKey(sessionId) && _activeSessions.Count == 1;
                if (accepted) _stopping = true;
                else if (!_stopping && Program.ClientStarted && _activeSessions.ContainsKey(sessionId)) _shutdownWhenIdleUntil = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            if (!accepted)
            {
                await SendResponseSuccessAsync(stream, requestId, new JObject { ["accepted"] = false, ["reason"] = "other_clients", ["pending_for_ms"] = Program.ClientStarted ? 5000 : 0 }, cancellationToken).ConfigureAwait(false);
                return;
            }
            try
            {
                await SendResponseSuccessAsync(stream, requestId, new JObject { ["accepted"] = true }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Program.RequestShutdown();
            }
            return;
        }

        if (operation == "devices.get")
        {
            if (!scopes.Contains("devices.read", StringComparer.Ordinal))
            {
                await SendResponseErrorAsync(stream, requestId, "SCOPE_REQUIRED", cancellationToken).ConfigureAwait(false);
                return;
            }
            RefreshDeviceCache();
            await SendResponseSuccessAsync(stream, requestId, new JObject
            {
                ["revision"] = Interlocked.Read(ref _deviceRevision),
                ["devices"] = GetDeviceSnapshot()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation is "lighting.acquire" or "lighting.release")
        {
            if (!scopes.Contains("lighting.write", StringComparer.Ordinal))
            {
                await SendResponseErrorAsync(stream, requestId, "SCOPE_REQUIRED", cancellationToken).ConfigureAwait(false);
                return;
            }
            var ids = (parameters["device_ids"] as JArray ?? new JArray())
                .Select(token => token.Type == JTokenType.String ? token.Value<string>() : null)
                .OfType<string>()
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if ((operation == "lighting.acquire" && ids.Length == 0) || ids.Length > 128)
            {
                await SendResponseErrorAsync(stream, requestId, "INVALID_DEVICE_IDS", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (operation == "lighting.acquire")
            {
                if (!TryAcquireLeases(sessionId, clientId, ids, out var granted, out var busy))
                {
                    await SendResponseErrorAsync(stream, requestId, "LEASE_BUSY", cancellationToken, new JObject { ["device_id"] = busy.FirstOrDefault() }).ConfigureAwait(false);
                    return;
                }
                await SendResponseSuccessAsync(stream, requestId, new JObject { ["granted_device_ids"] = new JArray(granted), ["busy_device_ids"] = new JArray(busy) }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var released = ReleaseLeases(sessionId, ids);
                await SendResponseSuccessAsync(stream, requestId, new JObject { ["released_device_ids"] = new JArray(released) }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        await SendResponseErrorAsync(stream, requestId, "OPERATION_UNSUPPORTED", cancellationToken).ConfigureAwait(false);
    }

    private bool ValidateAndRefreshLeases(string sessionId, JObject payload, out uint sequence, out string errorCode)
    {
        sequence = 0;
        errorCode = "INVALID_FRAME";
        var sequenceToken = payload["sequence"];
        var ttlValue = payload.Value<int?>("ttl_ms");
        if (sequenceToken == null || !uint.TryParse(sequenceToken.ToString(), out sequence)
            || ttlValue is not int ttlMs || ttlMs is < 1 or > 1000)
            return false;
        var targets = payload["targets"] as JArray;
        if (targets == null || targets.Count == 0 || targets.Count > 128)
            return false;

        Dictionary<string, HashSet<uint>> knownElements;
        lock (_deviceCacheLock)
            knownElements = _deviceElements;
        var deviceIds = new List<string>();
        foreach (var target in targets.OfType<JObject>())
        {
            var deviceId = target.Value<string>("device_id");
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;
            if (!knownElements.TryGetValue(deviceId, out var elementIds))
            {
                errorCode = "DEVICE_NOT_FOUND";
                return false;
            }
            var colors = target["colors"] as JArray;
            if (colors == null || colors.Count > 16384)
                return false;
            foreach (var color in colors.OfType<JObject>())
            {
                if (color["element_id"] == null || !uint.TryParse(color["element_id"]!.ToString(), out var elementId)
                    || !elementIds.Contains(elementId))
                {
                    errorCode = "ELEMENT_NOT_FOUND";
                    return false;
                }
                if (color.Value<int?>("r") is not int red || red is < 0 or > 255
                    || color.Value<int?>("g") is not int green || green is < 0 or > 255
                    || color.Value<int?>("b") is not int blue || blue is < 0 or > 255)
                    return false;
            }
            deviceIds.Add(deviceId);
        }

        lock (_leaseLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var deviceId in deviceIds.Distinct(StringComparer.Ordinal))
            {
                if (!_leasesByDevice.TryGetValue(deviceId, out var lease) || lease.SessionId != sessionId || lease.ExpiresAtUtc <= now)
                {
                    errorCode = "LEASE_REQUIRED";
                    return false;
                }
            }
            foreach (var deviceId in deviceIds.Distinct(StringComparer.Ordinal))
            {
                var lease = _leasesByDevice[deviceId];
                lease.ExpiresAtUtc = now.AddSeconds(5);
                lease.FrameExpiresAtUtc = now.AddMilliseconds(ttlMs);
            }
        }
        return true;
    }

    private bool TryAcquireLeases(string sessionId, string? clientId, IReadOnlyCollection<string> deviceIds, out string[] granted, out string[] busy)
    {
        HashSet<string> knownDeviceIds;
        lock (_deviceCacheLock)
            knownDeviceIds = _deviceElements.Keys.ToHashSet(StringComparer.Ordinal);
        var grantedList = new List<string>();
        var busyList = new List<string>();
        lock (_frameLock)
        lock (_leaseLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_activeSessions.TryGetValue(sessionId, out var requester))
            {
                granted = Array.Empty<string>();
                busy = deviceIds.ToArray();
                return false;
            }
            foreach (var id in deviceIds)
            {
                if (!knownDeviceIds.Contains(id)) { busyList.Add(id); continue; }
                if (_leasesByDevice.TryGetValue(id, out var current) && current.ExpiresAtUtc > now && current.SessionId != sessionId
                    && _activeSessions.TryGetValue(current.SessionId, out var owner)
                    && (owner.Priority > requester.Priority
                        || (owner.Priority == requester.Priority && (owner.ClientOrder < requester.ClientOrder
                            || (owner.ClientOrder == requester.ClientOrder && owner.Order < requester.Order)))))
                {
                    busyList.Add(id);
                    continue;
                }
                if (!_leasesByDevice.TryGetValue(id, out var existing) || existing.SessionId != sessionId || existing.ExpiresAtUtc <= now)
                    _leasesByDevice[id] = new Lease(sessionId, clientId ?? string.Empty, now.AddSeconds(5));
                grantedList.Add(id);
            }
        }
        granted = grantedList.ToArray();
        busy = busyList.ToArray();
        return granted.Length > 0;
    }

    private string[] ReleaseLeases(string sessionId, IEnumerable<string> deviceIds)
    {
        var released = new List<string>();
        lock (_frameLock)
        {
            lock (_leaseLock)
            {
                foreach (var deviceId in deviceIds)
                {
                    if (_leasesByDevice.TryGetValue(deviceId, out var lease) && lease.SessionId == sessionId)
                    {
                        _leasesByDevice.Remove(deviceId);
                        released.Add(deviceId);
                    }
                }
            }
            if (released.Count > 0) _restoreDevices(released);
        }
        return released.ToArray();
    }

    private void ReleaseSessionLeases(string sessionId)
    {
        string[] release;
        lock (_leaseLock)
            release = _leasesByDevice.Where(pair => pair.Value.SessionId == sessionId).Select(pair => pair.Key).ToArray();
        ReleaseLeases(sessionId, release);
    }

    private async Task LeaseCleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_frameLock)
            {
                var now = DateTimeOffset.UtcNow;
                var expiredBySession = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var expiredFrames = new List<string>();
                lock (_leaseLock)
                {
                    foreach (var pair in _leasesByDevice.ToArray())
                    {
                        if (pair.Value.ExpiresAtUtc <= now)
                        {
                            if (!expiredBySession.TryGetValue(pair.Value.SessionId, out var devices))
                                expiredBySession[pair.Value.SessionId] = devices = new List<string>();
                            devices.Add(pair.Key);
                        }
                        else if (pair.Value.FrameExpiresAtUtc is DateTimeOffset frameExpiry && frameExpiry <= now)
                        {
                            pair.Value.FrameExpiresAtUtc = null;
                            expiredFrames.Add(pair.Key);
                        }
                    }
                }
                foreach (var pair in expiredBySession)
                    ReleaseLeases(pair.Key, pair.Value);
                if (expiredFrames.Count > 0)
                    _restoreDevices(expiredFrames);
            }
        }
    }

    private async Task<long> SendDeviceListAsync(Stream stream, CancellationToken cancellationToken)
    {
        RefreshDeviceCache();
        var revision = Interlocked.Read(ref _deviceRevision);
        await WriteMessageAsync(stream, DeviceListType, new JObject
        {
            ["revision"] = revision,
            ["devices"] = GetDeviceSnapshot()
        }, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    private async Task SendDeviceChangedAsync(ActiveSession session)
    {
        try
        {
            await session.DeviceChangeLock.WaitAsync(session.Cancellation.Token).ConfigureAwait(false);
            try
            {
                var revision = Interlocked.Read(ref _deviceRevision);
                if (!session.Ready || revision <= session.DeviceRevision)
                    return;
                await WriteMessageAsync(session.Stream, DeviceChangedType, new JObject
                {
                    ["revision"] = revision,
                    ["devices"] = GetDeviceSnapshot()
                }, session.Cancellation.Token).ConfigureAwait(false);
                session.DeviceRevision = revision;
            }
            finally
            {
                session.DeviceChangeLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void RefreshDeviceCache()
    {
        var snapshot = _getDevices();
        var elements = snapshot
            .OfType<JObject>()
            .Select(device => new
            {
                Id = device.Value<string>("device_id"),
                ElementIds = (device["elements"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(element => uint.TryParse(element["element_id"]?.ToString(), out var id) ? (uint?)id : null)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToHashSet()
            })
            .Where(device => device.Id != null)
            .ToDictionary(device => device.Id!, device => device.ElementIds, StringComparer.Ordinal);
        lock (_deviceCacheLock)
        {
            _deviceSnapshot = (JArray)snapshot.DeepClone();
            _deviceElements = elements;
        }
    }

    private JArray GetDeviceSnapshot()
    {
        lock (_deviceCacheLock)
            return (JArray)_deviceSnapshot.DeepClone();
    }

    private async Task<Message?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { (byte)'M', (byte)'L', (byte)'V', (byte)'2' }))
            throw new InvalidDataException("Invalid MineLights V2 frame magic.");
        var major = ReadUInt16BigEndian(header, 4);
        var minor = ReadUInt16BigEndian(header, 6);
        var type = ReadUInt16BigEndian(header, 8);
        var flags = ReadUInt16BigEndian(header, 10);
        var length = ReadUInt32BigEndian(header, 12);
        if (_streamProtocolVersions.TryGetValue(stream, out var selectedVersion)
            && (major != ProtocolMajor || minor != selectedVersion.Minor))
            throw new InvalidDataException("MineLights V2 message does not use the negotiated protocol version.");
        if (flags != 0 || length > MaxPayloadLength || (IsControlMessage(type) && length > MaxControlPayloadLength))
            throw new InvalidDataException("MineLights V2 frame exceeds protocol limits.");
        var body = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, body, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Client closed during a V2 message.");
        var json = Encoding.UTF8.GetString(body);
        if (json.Length == 0)
            throw new InvalidDataException("MineLights V2 message payload is empty.");
        var payload = JsonConvert.DeserializeObject<JObject>(json) ?? throw new InvalidDataException("MineLights V2 payload must be a JSON object.");
        return new Message(major, minor, type, payload);
    }

    private async Task WriteMessageAsync(Stream stream, ushort type, JObject payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        if (body.Length > MaxPayloadLength || (IsControlMessage(type) && body.Length > MaxControlPayloadLength))
            throw new InvalidDataException("MineLights V2 message payload exceeds protocol limits.");
        var header = new byte[HeaderLength];
        header[0] = (byte)'M'; header[1] = (byte)'L'; header[2] = (byte)'V'; header[3] = (byte)'2';
        WriteUInt16BigEndian(header, 4, ProtocolMajor);
        WriteUInt16BigEndian(header, 6, _streamProtocolVersions.TryGetValue(stream, out var selectedVersion) ? selectedVersion.Minor : ProtocolMinor);
        WriteUInt16BigEndian(header, 8, type);
        WriteUInt16BigEndian(header, 10, 0);
        WriteUInt32BigEndian(header, 12, (uint)body.Length);
        var writeLock = _streamWriteLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                    return false;
                throw new EndOfStreamException("Client closed during a V2 frame.");
            }
            offset += read;
        }
        return true;
    }

    private static bool IsControlMessage(ushort type) => type is not (DeviceListType or DeviceChangedType or LightingFrameType);

    private async Task TrySendErrorAsync(Stream stream, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await WriteMessageAsync(stream, ErrorType, new JObject { ["error_code"] = errorCode, ["message"] = UserSafeError(errorCode) }, cancellationToken).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task TrySendAuthResultAsync(Stream stream, bool ok, IEnumerable<string> scopes, string? errorCode, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new JObject { ["ok"] = ok, ["scopes"] = new JArray(scopes) };
            if (errorCode != null) payload["error_code"] = errorCode;
            await WriteMessageAsync(stream, AuthResultType, payload, cancellationToken).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendResponseSuccessAsync(Stream stream, string requestId, JObject result, CancellationToken cancellationToken)
    {
        await WriteMessageAsync(stream, ResponseType, new JObject
        {
            ["request_id"] = requestId,
            ["ok"] = true,
            ["result"] = result
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendResponseErrorAsync(Stream stream, string requestId, string errorCode, CancellationToken cancellationToken, JObject? details = null)
    {
        var payload = new JObject
        {
            ["request_id"] = requestId,
            ["ok"] = false,
            ["error_code"] = errorCode
        };
        if (details != null) payload["details"] = details;
        await WriteMessageAsync(stream, ResponseType, payload, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] MakeAuthProof(byte[] key, string credentialType, string credentialId, string serverId, string clientId, byte[] serverNonce, byte[] clientNonce, ushort major, ushort minor)
    {
        using var message = new MemoryStream();
        var prefix = Encoding.UTF8.GetBytes($"MineLights Protocol V2\0{credentialType}\0{credentialId}\0{serverId}\0{clientId}");
        message.Write(prefix);
        message.Write(serverNonce);
        message.Write(clientNonce);
        message.WriteByte((byte)(major >> 8)); message.WriteByte((byte)major);
        message.WriteByte((byte)(minor >> 8)); message.WriteByte((byte)minor);
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(message.ToArray());
    }

    private static bool TryDecodeBase64Url(string? value, int expectedLength, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value) || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            return false;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            bytes = Convert.FromBase64String(base64);
            return bytes.Length == expectedLength;
        }
        catch { return false; }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeUuid(string? value) => Guid.TryParse(value, out var id) ? id.ToString("D").ToLowerInvariant() : string.Empty;

    private static string? NormalizeRequestId(JToken? token)
        => token?.Type == JTokenType.String && Guid.TryParse(token.Value<string>(), out var id)
            ? id.ToString("D").ToLowerInvariant()
            : null;

    private static string NormalizeAppId(string? value)
    {
        var normalized = SafeDisplay(value, 80).ToLowerInvariant();
        return normalized.Length > 0 && normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')
            ? normalized
            : string.Empty;
    }

    private static string SafeDisplay(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var safe = new string(value.Where(ch => !char.IsControl(ch)).Take(maxLength).ToArray()).Trim();
        return safe;
    }

    private static string UserSafeError(string code) => code switch
    {
        "VERSION_UNSUPPORTED" => "This client and server use incompatible protocol versions.",
        "PAIRING_REQUIRED" => "Pair this client with MineLights Server before connecting.",
        "PAIRING_EXPIRED" => "This pairing file has expired. Export a new one from MineLights Server.",
        "BAD_PROOF" => "Client authentication failed.",
        "UNKNOWN_CLIENT" => "This client is not paired with MineLights Server.",
        _ => "The MineLights V2 request could not be accepted."
    };

    private void RegisterAuthFailure(IPAddress? address)
    {
        if (address == null) return;
        var bucket = _authRateLimits.GetOrAdd(address.ToString(), _ => new RateLimitBucket());
        lock (bucket)
        {
            if (DateTimeOffset.UtcNow - bucket.WindowStart > TimeSpan.FromMinutes(1))
            {
                bucket.WindowStart = DateTimeOffset.UtcNow;
                bucket.Count = 0;
            }
            bucket.Count++;
            if (bucket.Count == 1 || bucket.Count % 5 == 0)
                Console.WriteLine($"[V2] Rejected unauthenticated connection from {address}; credentials omitted.");
        }
    }

    private void ResetAuthFailures(IPAddress? address)
    {
        if (address != null) _authRateLimits.TryRemove(address.ToString(), out _);
    }

    private void DisconnectClientSessions(string clientId)
    {
        foreach (var session in _activeSessions.Values.Where(session => session.ClientId == clientId))
            CloseSession(session);
        Console.WriteLine($"[V2] Revoked client {clientId}; credentials omitted.");
    }

    private void CancelRemoteSessions()
    {
        var sessions = _activeSessions.Values.Where(session => session.IsRemote).ToArray();
        foreach (var session in sessions)
            CloseSession(session);
        if (sessions.Length > 0)
            Console.WriteLine($"[V2] Closed {sessions.Length} remote session(s) because remote listener settings changed.");
    }

    private static void CloseSession(ActiveSession session)
    {
        try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        try { session.Stream.Dispose(); } catch { }
    }

    private string LoadOrCreateServerId()
    {
        var path = Path.Combine(_dataDirectory, "server-id.txt");
        if (File.Exists(path))
        {
            var id = NormalizeUuid(File.ReadAllText(path).Trim());
            if (id.Length > 0) return id;
        }
        var created = Guid.NewGuid().ToString("D").ToLowerInvariant();
        WriteProtectedFile(path, Encoding.ASCII.GetBytes(created));
        return created;
    }

    private byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
        }
        var key = RandomNumberGenerator.GetBytes(32);
        WriteProtectedFile(path, key);
        return key;
    }

    private X509Certificate2 LoadOrCreateCertificate()
    {
        var pfxPath = Path.Combine(_dataDirectory, "server-certificate.pfx");
        var passwordPath = Path.Combine(_dataDirectory, "server-certificate.password");
        if (File.Exists(pfxPath) && File.Exists(passwordPath))
        {
            var password = File.ReadAllText(passwordPath);
            return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), password, X509KeyStorageFlags.UserKeySet);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=MineLights Server {_serverId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var certificatePassword = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        WriteProtectedFile(passwordPath, Encoding.ASCII.GetBytes(certificatePassword));
        WriteProtectedFile(pfxPath, certificate.Export(X509ContentType.Pfx, certificatePassword));
        return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), certificatePassword, X509KeyStorageFlags.UserKeySet);
    }

    private void WriteEndpointDocument()
    {
        var path = Path.Combine(_dataDirectory, "endpoint.json");
        var endpoint = new JObject
        {
            ["schema_version"] = 1,
            ["server_id"] = _serverId,
            ["server_name"] = Environment.MachineName,
            ["host"] = "127.0.0.1",
            ["port"] = _actualPort,
            ["tls_certificate_sha256"] = _certificateFingerprint,
            ["protocol_major"] = ProtocolMajor,
            ["protocol_minor"] = ProtocolMinor
        };
        WriteProtectedFile(path, Encoding.UTF8.GetBytes(endpoint.ToString(Formatting.Indented)));
    }

    private void RemoveEndpointDocumentIfOwned()
    {
        var path = Path.Combine(_dataDirectory, "endpoint.json");
        try
        {
            if (!File.Exists(path)) return;
            var endpoint = JObject.Parse(File.ReadAllText(path));
            if (string.Equals((string?)endpoint["server_id"], _serverId, StringComparison.Ordinal)
                && (int?)endpoint["port"] == _actualPort
                && string.Equals((string?)endpoint["tls_certificate_sha256"], _certificateFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"[V2] Could not remove the local endpoint document during shutdown: {error.Message}");
        }
    }

    private void LoadCredentialStore()
    {
        var path = Path.Combine(_dataDirectory, "clients.json");
        try
        {
            if (!File.Exists(path)) return;
            var records = JsonConvert.DeserializeObject<List<StoredClientCredential>>(File.ReadAllText(path)) ?? new();
            foreach (var record in records)
            {
                if (!Guid.TryParse(record.ClientId, out _) || !TryDecodeBase64Url(record.ClientSecret, 32, out var secret)) continue;
                var id = Guid.Parse(record.ClientId).ToString("D").ToLowerInvariant();
                var scopes = record.Scopes.Where(scope => scope is "devices.read" or "lighting.write").Distinct(StringComparer.Ordinal).ToArray();
                if (!scopes.Contains("devices.read", StringComparer.Ordinal)) scopes = scopes.Append("devices.read").ToArray();
                _credentials[id] = new ClientCredential(id, record.AppId, record.AppVersion, record.DisplayName, secret, scopes, OneTime: false, Priority: Math.Clamp(record.Priority, -1, 1));
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"[V2] Could not load client registry: {error.Message}");
        }
    }

    private void SaveCredentialStore()
    {
        var path = Path.Combine(_dataDirectory, "clients.json");
        var records = _credentials.Values
            .Where(record => !record.OneTime)
            .Select(record => new StoredClientCredential
            {
                ClientId = record.ClientId,
                ClientSecret = Base64UrlEncode(record.Secret),
                AppId = record.AppId,
                AppVersion = record.AppVersion,
                DisplayName = record.DisplayName,
                Scopes = record.Scopes.ToArray(),
                Priority = record.Priority
            })
            .OrderBy(record => record.ClientId, StringComparer.Ordinal)
            .ToList();
        WriteProtectedFile(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(records, Formatting.Indented)));
    }

    private void WriteProtectedFile(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        ProtectFile(temp);
        File.Move(temp, path, overwrite: true);
        ProtectFile(path);
    }

    private static void ProtectDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Could not determine current Windows user SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Could not determine current Windows user SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool IsSequenceNewer(uint candidate, uint previous) => candidate != previous && unchecked((int)(candidate - previous)) > 0;

    private static ushort ReadUInt16BigEndian(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    private static uint ReadUInt32BigEndian(byte[] bytes, int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    private static void WriteUInt16BigEndian(byte[] bytes, int offset, ushort value) { bytes[offset] = (byte)(value >> 8); bytes[offset + 1] = (byte)value; }
    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value) { bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value; }

    private sealed record Message(ushort Major, ushort Minor, ushort Type, JObject Payload);
    private sealed record NegotiatedProtocolVersion(ushort Minor);
    private sealed class ActiveSession(string clientId, CancellationTokenSource cancellation, bool isRemote, Stream stream)
    {
        public string ClientId { get; } = clientId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool IsRemote { get; } = isRemote;
        public Stream Stream { get; } = stream;
        public SemaphoreSlim DeviceChangeLock { get; } = new(1, 1);
        public volatile bool Ready;
        public volatile int Priority;
        public long ClientOrder;
        public long Order;
        private long _deviceRevision;
        public long DeviceRevision
        {
            get => Interlocked.Read(ref _deviceRevision);
            set => Interlocked.Exchange(ref _deviceRevision, value);
        }
    }
    private sealed record ClientCredential(string ClientId, string AppId, string AppVersion, string DisplayName, byte[] Secret, IReadOnlyCollection<string> Scopes, bool OneTime, int Priority = 0);
    private sealed class PairingCredential(byte[] code, DateTimeOffset expiresAtUtc)
    {
        public byte[] Code { get; } = code;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public int Attempts { get; set; }
    }
    private sealed class Lease(string sessionId, string clientId, DateTimeOffset expiresAtUtc)
    {
        public string SessionId { get; } = sessionId;
        public string ClientId { get; } = clientId;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        public DateTimeOffset? FrameExpiresAtUtc { get; set; }
    }
    private sealed class RateLimitBucket
    {
        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.UtcNow;
        public int Count { get; set; }
    }
    private sealed class StoredClientCredential
    {
        [JsonProperty("client_id")] public string ClientId { get; set; } = string.Empty;
        [JsonProperty("client_secret")] public string ClientSecret { get; set; } = string.Empty;
        [JsonProperty("app_id")] public string AppId { get; set; } = string.Empty;
        [JsonProperty("app_version")] public string AppVersion { get; set; } = string.Empty;
        [JsonProperty("display_name")] public string DisplayName { get; set; } = string.Empty;
        [JsonProperty("scopes")] public string[] Scopes { get; set; } = Array.Empty<string>();
        [JsonProperty("priority")] public int Priority { get; set; }
    }
}
