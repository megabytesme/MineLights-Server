public class ServerConfig
{
    // Absent in older server configurations until the owner reviews filters.
    public int ConfigSchemaVersion { get; set; }
    public List<string> EnabledIntegrations { get; set; }
    public List<string> DisabledDevices { get; set; } = new();
    public bool AllowLegacyLocalClients { get; set; } = false;
    public bool RemoteAccessEnabled { get; set; } = false;
    public List<string> RemoteListenAddresses { get; set; } = new();
    public int RemotePort { get; set; } = 63215;
}
