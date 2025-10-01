using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
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
        lightingServer = new LightingServer(Shutdown);
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
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, OnExit);
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
        if (trayIcon?.ContextMenuStrip?.InvokeRequired ?? false)
        {
            trayIcon.ContextMenuStrip.Invoke(new MethodInvoker(Shutdown));
            return;
        }

        try
        {
            lightingServer.Stop();
        }
        catch { }

        try
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }
        catch { }

        Application.ExitThread();
    }

    private void OnExit(object? sender, EventArgs e) => Shutdown();
}
