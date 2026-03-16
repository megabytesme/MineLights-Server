using Newtonsoft.Json;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using static LightingServer;
using MethodInvoker = System.Windows.Forms.MethodInvoker;

static class Program
{
    private static readonly string MutexName = "{8E2D4B6C-7F01-4A5D-9C2E-2A47A5A0A9A3}";
    private static Mutex? _mutex;

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

        Application.Exit();
    }

    public static void RequestShutdown()
    {
        Application.Exit();
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

    public MyAppContext()
    {
        lightingServer = new LightingServer();
        lightingServer.Start();

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

    private void Shutdown()
    {
        Application.Exit();
    }

    private void OnExit(object? sender, EventArgs e) => Shutdown();
}
