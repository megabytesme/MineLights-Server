using System.Text;

public sealed class ServerLogger : TextWriter
{
    private static readonly Lazy<ServerLogger> _instance = new(() => new ServerLogger());
    public static ServerLogger Instance => _instance.Value;

    private readonly StreamWriter _fileWriter;
    private readonly TextWriter _originalConsoleOut;
    private readonly string _logFilePath;

    public override Encoding Encoding => Encoding.UTF8;
    public string LogFilePath => _logFilePath;

    private ServerLogger()
    {
        _originalConsoleOut = Console.Out;

        try
        {
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);

            var logFiles = new DirectoryInfo(logDirectory)
                .GetFiles("MineLights_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            foreach (var oldFile in logFiles.Skip(5))
            {
                try
                {
                    oldFile.Delete();
                }
                catch { }
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _logFilePath = Path.Combine(logDirectory, $"MineLights_{timestamp}.log");

            var fileStream = new FileStream(
                _logFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite
            );
            _fileWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };

            Log("----------------------------------------------------------");
            Log($"Log session started at {DateTime.Now:F}");
            Log("----------------------------------------------------------");
        }
        catch (Exception ex)
        {
            _originalConsoleOut.WriteLine($"FATAL: Failed to initialize file logger. {ex.Message}");
            throw;
        }
    }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            _originalConsoleOut.WriteLine();
            _fileWriter.WriteLine();
            return;
        }

        string formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _originalConsoleOut.WriteLine(formattedMessage);
        _fileWriter.WriteLine(formattedMessage);
    }

    public void Log(string message) => WriteLine(message);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _fileWriter?.Dispose();
    }
}
