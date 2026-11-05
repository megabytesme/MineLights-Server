using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Newtonsoft.Json;

/// <summary>
/// Persists per-install device IDs. Hardware identifiers are used when RGB.NET exposes them;
/// otherwise a stable descriptor key and persisted duplicate slots are used.
/// Devices with no hardware identifier and byte-for-byte identical descriptors cannot be bound
/// to the same physical unit across enumeration changes; their persisted slots may exchange units.
/// </summary>
internal sealed class StableDeviceIdRegistry
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, string>> _assignments = new(StringComparer.Ordinal);

    public StableDeviceIdRegistry()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineLights", "v2");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "device-identities.json");
        Load();
    }

    public IReadOnlyDictionary<object, string> Resolve(IEnumerable<DeviceDescriptor> devices)
    {
        lock (_lock)
        {
            var changed = false;
            var result = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (var group in devices.GroupBy(device => device.IdentityKey, StringComparer.Ordinal))
            {
                if (!_assignments.TryGetValue(group.Key, out var entries))
                    _assignments[group.Key] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var disambiguatorGroup in group
                    .OrderBy(device => device.Disambiguator, StringComparer.Ordinal)
                    .ThenBy(device => device.StableSortKey, StringComparer.Ordinal)
                    .GroupBy(device => device.Disambiguator, StringComparer.Ordinal))
                {
                    var ordered = disambiguatorGroup.OrderBy(device => device.StableSortKey, StringComparer.Ordinal).ToArray();
                    for (var index = 0; index < ordered.Length; index++)
                    {
                        var assignmentKey = ordered.Length == 1
                            ? disambiguatorGroup.Key
                            : $"{disambiguatorGroup.Key}\0slot:{index}";
                        if (!entries.TryGetValue(assignmentKey, out var id))
                        {
                            if (index == 0 && entries.TryGetValue(disambiguatorGroup.Key, out var priorId))
                                id = priorId;
                            else
                                id = Guid.NewGuid().ToString("N").ToLowerInvariant();
                            entries[assignmentKey] = id;
                            changed = true;
                        }
                        result[ordered[index].Device] = "rgbnet-" + id;
                    }
                }
            }
            if (changed)
                Save();
            return result;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_path));
            if (loaded == null)
                return;
            foreach (var pair in loaded)
                _assignments[pair.Key] = new Dictionary<string, string>(pair.Value, StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[V2] Could not load the persisted device identity map: {error.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(_assignments, Formatting.Indented), new UTF8Encoding(false));
            ProtectFile(tempPath);
            File.Move(tempPath, _path, overwrite: true);
            ProtectFile(_path);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[V2] Could not persist device identities: {error.Message}");
        }
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null)
                return;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch { }
    }

    internal sealed record DeviceDescriptor(object Device, string IdentityKey, string Disambiguator, string StableSortKey);
}
