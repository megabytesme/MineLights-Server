using Newtonsoft.Json;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Principal;
using static LightingServer;
using MethodInvoker = System.Windows.Forms.MethodInvoker;

static class Program
{
    private static readonly string MutexName = "{8E2D4B6C-7F01-4A5D-9C2E-2A47A5A0A9A3}";
    private static Mutex? _mutex;
    private static MyAppContext? _context;
    public static bool ClientStarted { get; private set; }
    // Correlation identifier only; authentication always uses separate V2 credentials.
    public static string? ClientLaunchId { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--restart-helper")
        {
            int parentPid = int.Parse(args[1]);
            bool asAdmin = args.Any(a => a == "--elevated");

            try
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch { }

            var psi = new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = asAdmin ? "runas" : "open",
            };
            Process.Start(psi);
            return;
        }

        ClientStarted = args.Contains("--client-started", StringComparer.Ordinal);
        ClientLaunchId = ClientStarted && Guid.TryParse(Environment.GetEnvironmentVariable("MINELIGHTS_LAUNCH_ID"), out var launchId)
            ? launchId.ToString("D").ToLowerInvariant() : null;
        Environment.SetEnvironmentVariable("MINELIGHTS_LAUNCH_ID", null);
        Console.SetOut(ServerLogger.Instance);

        _mutex = new Mutex(initiallyOwned: false, name: MutexName, out bool createdNew);
        bool mutexAcquired = false;
        try
        {
            mutexAcquired = _mutex.WaitOne(TimeSpan.FromSeconds(2), exitContext: false);
        }
        catch { }

        if (!mutexAcquired)
        {
            MessageBox.Show(
                "MineLights Server is already running.",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        NativeDllLoader.LoadNativeSDKs();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MyAppContext());

        try
        {
            _mutex.ReleaseMutex();
        }
        catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    public static void RequestRestart(bool asAdmin)
    {
        var current = Process.GetCurrentProcess();
        var args = $"--restart-helper {current.Id}" + (asAdmin ? " --elevated" : "");
        var psi = new ProcessStartInfo(Application.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "open",
            Arguments = args,
        };
        Process.Start(psi);

        RequestShutdown();
    }

    internal static void RegisterContext(MyAppContext context) => _context = context;

    public static void RequestShutdown()
    {
        var context = _context;
        if (context != null) context.RequestShutdown();
        else Application.Exit();
    }

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

public class MyAppContext : ApplicationContext
{
    private NotifyIcon trayIcon;
    private LightingServer lightingServer;
    private ToolStripMenuItem legacyClientsItem = null!;
    private ToolStripMenuItem remoteAccessItem = null!;
    private ToolStripMenuItem revokeClientsItem = null!;
    private int _cleanedUp;

    public MyAppContext()
    {
        lightingServer = new LightingServer();
        var iconStream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("MineLights.resources.app_icon.ico");
        if (iconStream == null)
            throw new FileNotFoundException("Tray icon resource not found.");

        trayIcon = new NotifyIcon
        {
            Icon = new Icon(iconStream),
            Text = "MineLights Server",
            ContextMenuStrip = new ContextMenuStrip(),
            Visible = true,
        };

        trayIcon.ContextMenuStrip.Items.Add("View Current Log", null, OnViewLogs);
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        var serverSettings = new ToolStripMenuItem("Server Connections");
        legacyClientsItem = new ToolStripMenuItem("Allow legacy local clients")
        {
            CheckOnClick = false,
            Checked = lightingServer.AllowLegacyLocalClients,
            ToolTipText = "Allows unauthenticated V1 lighting from loopback only. Stop requests from V2 clients are refused while legacy access is enabled."
        };
        legacyClientsItem.Click += OnToggleLegacyClients;
        remoteAccessItem = new ToolStripMenuItem("Remote access…", null, OnConfigureRemoteAccess);
        serverSettings.DropDownItems.Add(legacyClientsItem);
        serverSettings.DropDownItems.Add(remoteAccessItem);
        serverSettings.DropDownItems.Add(new ToolStripMenuItem("Review device filters…", null, (_, _) => ReviewDeviceFilters(false)));
        serverSettings.DropDownItems.Add(new ToolStripMenuItem("Export one-time pairing file…", null, OnExportPairingFile));
        revokeClientsItem = new ToolStripMenuItem("Revoke paired client");
        revokeClientsItem.DropDownOpening += OnRevokeClientsMenuOpening;
        serverSettings.DropDownItems.Add(revokeClientsItem);
        var priorityMenu = new ToolStripMenuItem("Client lighting priority");
        priorityMenu.DropDownOpening += (_, _) =>
        {
            priorityMenu.DropDownItems.Clear();
            var clients = lightingServer.ListV2Clients();
            if (clients.Count == 0)
                priorityMenu.DropDownItems.Add(new ToolStripMenuItem("No paired clients") { Enabled = false });
            foreach (var client in clients)
            {
                var id = client.Value<string>("client_id") ?? string.Empty;
                var name = client.Value<string>("display_name");
                if (string.IsNullOrWhiteSpace(name)) name = client.Value<string>("app_id") ?? "Client";
                var submenu = new ToolStripMenuItem($"{name} ({id[..Math.Min(id.Length, 8)]}…)");
                var currentPriority = client.Value<int?>("priority") ?? 0;
                foreach (var (label, value) in new[] { ("High", 1), ("Normal", 0), ("Low", -1) })
                {
                    var choice = new ToolStripMenuItem(label) { Checked = currentPriority == value };
                    choice.Click += (_, _) => lightingServer.SetV2ClientPriority(id, value);
                    submenu.DropDownItems.Add(choice);
                }
                priorityMenu.DropDownItems.Add(submenu);
            }
        };
        serverSettings.DropDownItems.Add(priorityMenu);
        serverSettings.DropDownOpening += (_, _) => RefreshConnectionMenu();
        trayIcon.ContextMenuStrip.Items.Add(serverSettings);
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        var logs = Directory.GetFiles(logsDir, "MineLights_*.log")
                            .OrderByDescending(File.GetCreationTimeUtc)
                            .Take(5);

        foreach (var log in logs)
        {
            trayIcon.ContextMenuStrip.Items.Add(
                Path.GetFileName(log),
                null,
                (s, e) => Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })
            );
        }
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        var supportMenu = new ToolStripMenuItem("Support");
        supportMenu.DropDownItems.Add("Create Device Snapshot", null, OnCreateSnapshot);
        var emulationMenu = new ToolStripMenuItem("Emulation");
        emulationMenu.DropDownItems.Add("Load Device Snapshot…", null, OnLoadSnapshot);
        emulationMenu.DropDownItems.Add("Exit Emulation", null, OnExitEmulation);
        supportMenu.DropDownItems.Add(emulationMenu);
        trayIcon.ContextMenuStrip.Items.Add(supportMenu);
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, OnExit);

        _ = trayIcon.ContextMenuStrip.Handle;
        Program.RegisterContext(this);
        lightingServer.LegacyClientDetected += OnLegacyClientDetected;
        lightingServer.Start();
        RefreshConnectionMenu();
        if (lightingServer.RequiresLegacyFilterReview)
            trayIcon.ContextMenuStrip.BeginInvoke((Action)(() => ReviewDeviceFilters(true)));
    }

    private void ReviewDeviceFilters(bool legacyMigration)
    {
        var filters = lightingServer.GetGlobalDeviceFilters();
        static string Summarize(string[] values) => values.Length == 0 ? "(none)" :
            string.Join(", ", values.Take(12)) + (values.Length > 12 ? $" and {values.Length - 12} more" : "");
        var introduction = legacyMigration
            ? "An older MineLights Server saved device settings from a legacy client. These settings currently restrict every client, including V2 clients."
            : "These server-wide filters restrict every client, in addition to each client's own settings.";
        var message = $"{introduction}\n\nEnabled server providers: {Summarize(filters.Providers)}\nServer-disabled devices: {Summarize(filters.DisabledDevices)}\n\nYes: Enable all supported providers and clear the server-wide disabled-device list. This may interrupt lighting while devices refresh.\nNo: Keep the current server-wide filters.\nCancel: Decide later.\n\nIndividual clients retain their own device and integration settings.";
        var answer = MessageBox.Show(message, "Review server device filters", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes && answer != DialogResult.No) return;
        try
        {
            lightingServer.ReviewGlobalDeviceFilters(answer == DialogResult.Yes);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Could not update server device filters.\n\n{error.Message}", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnToggleLegacyClients(object? sender, EventArgs e)
    {
        var enable = !lightingServer.AllowLegacyLocalClients;
        if (enable && MessageBox.Show(
                "Legacy V1 clients have no authentication and can send lighting to devices from local software. This permission is restricted to loopback. V1 shutdown and restart commands remain disabled. While legacy access is enabled, V2 clients cannot stop the server; disable legacy access or exit using the tray menu. After allowing access, refresh devices in MineLights or restart the client if devices do not appear. Allow legacy local lighting clients?",
                "Allow legacy MineLights clients",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        SetLegacyCompatibility(enable);
    }

    private void OnLegacyClientDetected()
    {
        var contextMenu = trayIcon.ContextMenuStrip;
        if (contextMenu == null || contextMenu.IsDisposed)
            return;
        try
        {
            contextMenu.BeginInvoke((Action)(() =>
            {
                if (lightingServer.AllowLegacyLocalClients)
                    return;
                var answer = MessageBox.Show(
                    "An unauthenticated legacy MineLights client tried to connect from this computer. Allow loopback lighting for legacy clients? The server will not accept legacy shutdown or restart commands. While legacy access is enabled, V2 clients cannot stop the server; disable legacy access or exit using the tray menu. After allowing access, refresh devices in MineLights or restart the client if devices do not appear.",
                    "Legacy client blocked",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                    SetLegacyCompatibility(true);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void SetLegacyCompatibility(bool enabled)
    {
        try
        {
            lightingServer.SetLegacyLocalCompatibility(enabled);
            legacyClientsItem.Checked = lightingServer.AllowLegacyLocalClients;
        }
        catch (Exception error)
        {
            MessageBox.Show($"Could not change legacy client access.\n\n{error.Message}", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshConnectionMenu()
    {
        legacyClientsItem.Checked = lightingServer.AllowLegacyLocalClients;
        remoteAccessItem.Text = lightingServer.RemoteAccessEnabled ? "Remote access… (enabled)" : "Remote access… (local only)";
    }

    private void OnConfigureRemoteAccess(object? sender, EventArgs e)
    {
        var available = GetAvailableNetworkAddresses();
        using var dialog = new Form
        {
            Text = "MineLights remote access",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 390)
        };
        var explanation = new Label
        {
            Text = "Choose the network interfaces where authenticated V2 clients may connect. This does not configure router port forwarding. Remote pairing requires a one-time file exported separately.",
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(416, 60)
        };
        var addresses = new CheckedListBox
        {
            CheckOnClick = true,
            Location = new Point(12, 80),
            Size = new Size(416, 220),
            HorizontalScrollbar = true
        };
        var selected = lightingServer.RemoteListenAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var address in available)
            addresses.Items.Add(address, selected.Contains(address.Address.ToString()));
        var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(12, 315) };
        var port = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(lightingServer.RemotePort, 1, 65535),
            Location = new Point(54, 311),
            Width = 100
        };
        var save = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(255, 345), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(345, 345), Width = 80 };
        dialog.Controls.AddRange(new Control[] { explanation, addresses, portLabel, port, save, cancel });
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog() != DialogResult.OK)
            return;
        var selectedAddresses = addresses.CheckedItems.Cast<NetworkAddressChoice>().Select(item => item.Address).ToArray();
        if (selectedAddresses.Length > 0
            && MessageBox.Show(
                $"Enable authenticated remote connections on {selectedAddresses.Length} selected interface(s), port {port.Value}? Only paired V2 clients will be accepted.",
                "Enable remote access",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            lightingServer.ConfigureRemoteAccess(selectedAddresses, (int)port.Value);
            RefreshConnectionMenu();
            MessageBox.Show(
                selectedAddresses.Length == 0
                    ? "Remote access is disabled. The authenticated local V2 endpoint remains available."
                    : $"Remote access is enabled on port {lightingServer.RemotePort}. If the selected port was unavailable, MineLights Server selected another port; check the pairing file before importing it.",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Could not update remote access.\n\n{error.Message}", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static List<NetworkAddressChoice> GetAvailableNetworkAddresses()
    {
        var addresses = new List<NetworkAddressChoice>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                        || IPAddress.IsLoopback(address)
                        || address.Equals(IPAddress.Any)
                        || address.Equals(IPAddress.IPv6Any)
                        || address.IsIPv6LinkLocal
                        || address.IsIPv6Multicast)
                        continue;
                    if (!addresses.Any(existing => existing.Address.Equals(address)))
                        addresses.Add(new NetworkAddressChoice($"{network.Name} — {address}", address));
                }
            }
            catch { }
        }
        return addresses.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnExportPairingFile(object? sender, EventArgs e)
    {
        if (!lightingServer.RemoteAccessEnabled)
        {
            MessageBox.Show("Enable remote access and select an interface before exporting a pairing file.", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var enabledAddresses = lightingServer.RemoteListenAddresses
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .OfType<IPAddress>()
            .ToHashSet();
        var addressChoices = GetAvailableNetworkAddresses()
            .Where(choice => enabledAddresses.Contains(choice.Address))
            .ToList();
        if (addressChoices.Count == 0)
        {
            MessageBox.Show("None of the enabled remote addresses is available on this computer now. Reconfigure remote access before exporting a pairing file.", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var selectedAddress = addressChoices.Count == 1 ? addressChoices[0] : ChoosePairingAddress(addressChoices);
        if (selectedAddress == null)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Export one-time MineLights pairing file",
            Filter = "MineLights pairing files (*.mlpair)|*.mlpair|All files (*.*)|*.*",
            FileName = $"minelights-pairing-{DateTime.Now:yyyyMMdd-HHmmss}.mlpair",
            AddExtension = true,
            DefaultExt = "mlpair"
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;
        try
        {
            var path = lightingServer.ExportV2PairingFile(dialog.FileName, selectedAddress.Address);
            var exported = JObject.Parse(File.ReadAllText(path));
            var expiresUtc = DateTimeOffset.Parse(exported.Value<string>("expires_at_utc")!, System.Globalization.CultureInfo.InvariantCulture);
            MessageBox.Show(
                $"One-time pairing file exported to:\n{path}\n\nExpires: {expiresUtc.ToLocalTime():g} local time ({expiresUtc:O}). The secret is consumed after registration. Transfer the file securely and import it in MineLights. Do not post it publicly.",
                "Pairing file exported",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Could not export the pairing file.\n\n{error.Message}", "MineLights", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private NetworkAddressChoice? ChoosePairingAddress(IReadOnlyList<NetworkAddressChoice> choices)
    {
        using var dialog = new Form
        {
            Text = "Choose pairing address",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(430, 142)
        };
        var explanation = new Label
        {
            Text = "Choose the network address the other computer can reach. The pairing file will contain this address.",
            Location = new Point(12, 12),
            Size = new Size(406, 40)
        };
        var address = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(12, 57),
            Width = 406
        };
        foreach (var choice in choices)
            address.Items.Add(choice);
        address.SelectedIndex = 0;
        var useAddress = new Button { Text = "Continue", DialogResult = DialogResult.OK, Location = new Point(248, 100), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(338, 100), Width = 80 };
        dialog.Controls.AddRange(new Control[] { explanation, address, useAddress, cancel });
        dialog.AcceptButton = useAddress;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog() != DialogResult.OK)
            return null;
        return address.SelectedItem as NetworkAddressChoice;
    }

    private void OnRevokeClientsMenuOpening(object? sender, EventArgs e)
    {
        revokeClientsItem.DropDownItems.Clear();
        var clients = lightingServer.ListV2Clients();
        if (clients.Count == 0)
        {
            revokeClientsItem.DropDownItems.Add(new ToolStripMenuItem("No paired clients") { Enabled = false });
            return;
        }
        foreach (var client in clients)
        {
            var clientId = client.Value<string>("client_id") ?? string.Empty;
            var app = client.Value<string>("display_name");
            if (string.IsNullOrWhiteSpace(app)) app = client.Value<string>("app_id") ?? "Client";
            var item = new ToolStripMenuItem($"{app} ({clientId[..Math.Min(clientId.Length, 8)]}…)");
            item.Click += (_, _) =>
            {
                if (MessageBox.Show($"Revoke {app}? Its active V2 sessions will be closed and it will need to pair again.", "Revoke paired client", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    lightingServer.RevokeV2Client(clientId);
            };
            revokeClientsItem.DropDownItems.Add(item);
        }
    }

    private sealed record NetworkAddressChoice(string Label, IPAddress Address)
    {
        public override string ToString() => Label;
    }

    private void OnExitEmulation(object? sender, EventArgs e)
    {
        try
        {
            lightingServer.ExitEmulation();

            MessageBox.Show(
                "Emulation mode exited. Real hardware restored.",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to exit emulation.\n\n{ex.Message}",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void OnLoadSnapshot(object? sender, EventArgs e)
    {
        try
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Load MineLights Device Snapshot",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            if (ofd.ShowDialog() != DialogResult.OK)
                return;

            string json = File.ReadAllText(ofd.FileName);
            var snapshot = JsonConvert.DeserializeObject<RawSnapshotFile>(json);

            if (snapshot == null)
            {
                MessageBox.Show(
                    "Invalid snapshot file.",
                    "MineLights",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            lightingServer.LoadSnapshotForEmulation(snapshot);

            MessageBox.Show(
                "Snapshot loaded. Emulation mode active.",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to load snapshot.\n\n{ex.Message}",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void OnCreateSnapshot(object? sender, EventArgs e)
    {
        try
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Save MineLights Device Snapshot",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                FileName = $"minelights_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            if (sfd.ShowDialog() != DialogResult.OK)
                return;

            var snapshot = lightingServer.BuildRawSnapshot();

            string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

            File.WriteAllText(sfd.FileName, json);

            MessageBox.Show(
                "Device snapshot created successfully.",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to create device snapshot.\n\nError: {ex.Message}",
                "MineLights",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void OnViewLogs(object? sender, EventArgs e)
    {
        try
        {
            string logPath = ServerLogger.Instance.LogFilePath;
            if (File.Exists(logPath))
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            else
                MessageBox.Show(
                    "Log file not found.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open log file.\n\nError: {ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    public void RequestShutdown()
    {
        var menu = trayIcon.ContextMenuStrip;
        if (menu == null || menu.IsDisposed) return;
        try
        {
            if (menu.InvokeRequired) menu.BeginInvoke((Action)ExitThread);
            else ExitThread();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            if (Interlocked.Exchange(ref _cleanedUp, 1) == 0)
            {
                lightingServer.LegacyClientDetected -= OnLegacyClientDetected;
                try { lightingServer.Stop(); }
                finally
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            }
        }
        finally { base.ExitThreadCore(); }
    }

    private void OnExit(object? sender, EventArgs e) => RequestShutdown();
}
