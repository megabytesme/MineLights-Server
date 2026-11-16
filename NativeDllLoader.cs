using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using RGB.NET.Devices.Corsair;
using RGB.NET.Devices.Logitech;
using RGB.NET.Devices.Msi;
using RGB.NET.Devices.Wooting;

public static class NativeDllLoader
{
    public static void LoadNativeSDKs()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            Console.WriteLine("[Loader] All Embedded Resources:");
            foreach (var name in assembly.GetManifestResourceNames())
            {
                Console.WriteLine($"[Loader] -> Found resource: {name}");
            }

            string version = assembly.GetName().Version?.ToString()
                ?? throw new InvalidOperationException("The server assembly version is unavailable.");

            string nativeLibraryRoot;
            if (Program.IsRunningAsAdmin())
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                nativeLibraryRoot = PrepareProtectedCacheRoot(programFiles);
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                    throw new InvalidOperationException("The local application data directory is unavailable.");

                nativeLibraryRoot = Path.Combine(localAppData, "MineLights", "NativeLibs");
            }

            string versionPath = Path.Combine(nativeLibraryRoot, "x64", version);
            Directory.CreateDirectory(versionPath);
            if (Program.IsRunningAsAdmin())
                EnsureNoReparsePoints(versionPath);
            Console.WriteLine($"[Loader] Using versioned native library path: {versionPath}");

            ConfigureNativeSdkPaths(versionPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Loader] FATAL ERROR: {ex.Message}");
            MessageBox.Show(
                $"A critical error occurred while loading native libraries:\n\n{ex.Message}\n\nThe application will now exit.",
                "MineLights Server Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            Environment.Exit(1);
        }
    }

    internal static string ExtractVerifiedResource(byte[] resourceBytes, string versionPath, string fileName)
    {
        ArgumentNullException.ThrowIfNull(resourceBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("The native library name must not contain a path.", nameof(fileName));

        byte[] expectedHash = SHA256.HashData(resourceBytes);
        string hashDirectory = Convert.ToHexString(expectedHash).ToLowerInvariant();
        string outputDirectory = Path.Combine(versionPath, hashDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (Program.IsRunningAsAdmin())
        {
            EnsureNoReparsePoints(outputDirectory);
            VerifyProtectedCacheObjectSecurity(new DirectoryInfo(outputDirectory).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));
        }

        string outputPath = Path.Combine(outputDirectory, fileName);
        if (FileMatchesHash(outputPath, expectedHash))
        {
            if (Program.IsRunningAsAdmin())
                VerifyProtectedCacheObjectSecurity(new FileInfo(outputPath).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner));
            return outputPath;
        }

        string temporaryPath = Path.Combine(outputDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fileStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.WriteThrough
            ))
            {
                fileStream.Write(resourceBytes);
                fileStream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            catch (Exception exception)
                when ((exception is IOException || exception is UnauthorizedAccessException)
                    && WaitForExpectedCacheFile(outputPath, expectedHash))
            {
                // Another process completed the same content-addressed extraction first.
            }

            if (!WaitForExpectedCacheFile(outputPath, expectedHash))
                throw new IOException($"Extracted native library '{fileName}' does not match its embedded resource.");
            if (Program.IsRunningAsAdmin())
                VerifyProtectedCacheObjectSecurity(new FileInfo(outputPath).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner));

            return outputPath;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (FileNotFoundException)
            {
                // The temporary file was moved into place.
            }
        }
    }

    internal static void SetOnlyVerifiedSearchPath(IList<string> possiblePaths, string verifiedPath)
    {
        ArgumentNullException.ThrowIfNull(possiblePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPath);
        if (!Path.IsPathFullyQualified(verifiedPath))
            throw new ArgumentException("The verified native library path must be absolute.", nameof(verifiedPath));

        possiblePaths.Clear();
        possiblePaths.Add(verifiedPath);
    }

    internal static string PrepareProtectedCacheRoot(string programFilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFilesPath);
        if (!Path.IsPathFullyQualified(programFilesPath))
            throw new ArgumentException("The Program Files path must be absolute.", nameof(programFilesPath));

        string fullProgramFilesPath = Path.GetFullPath(programFilesPath);
        EnsureNoReparsePoints(fullProgramFilesPath);
        if (!Program.IsRunningAsAdmin())
            throw new UnauthorizedAccessException("The protected native library cache can only be prepared by an elevated process.");

        string cacheRoot = Path.Combine(fullProgramFilesPath, "MineLights.NativeDllCache");
        Directory.CreateDirectory(cacheRoot);
        EnsureNoReparsePoints(cacheRoot);

        DirectorySecurity protectedSecurity = CreateProtectedDirectorySecurity();
        new DirectoryInfo(cacheRoot).SetAccessControl(protectedSecurity);
        EnsureNoReparsePoints(cacheRoot);
        VerifyProtectedDirectorySecurity(new DirectoryInfo(cacheRoot).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));

        return cacheRoot;
    }

    internal static void ConfigureNativeSdkPaths(string versionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionPath);
        Assembly assembly = Assembly.GetExecutingAssembly();
        LoadSdk(assembly, "Corsair", "MineLights.resources.x64.iCUESDK.x64_2019.dll", "iCUESDK.x64_2019.dll",
            versionPath, CorsairDeviceProvider.PossibleX64NativePaths);
        LoadSdk(assembly, "MSI", "MineLights.resources.x64.MysticLight_SDK_x64.dll", "MysticLight_SDK_x64.dll",
            versionPath, MsiDeviceProvider.PossibleX64NativePaths);
        LoadSdk(assembly, "Logitech", "MineLights.resources.x64.LogitechLedEnginesWrapper.dll", "LogitechLedEnginesWrapper.dll",
            versionPath, LogitechDeviceProvider.PossibleX64NativePaths);
        LoadSdk(assembly, "Wooting", "MineLights.resources.x64.wooting-rgb-sdk.dll", "wooting-rgb-sdk.dll",
            versionPath, WootingDeviceProvider.PossibleX64NativePathsWindows);
    }

    internal static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inheritToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.SetOwner(administratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid, FileSystemRights.FullControl, inheritToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administratorsSid, FileSystemRights.FullControl, inheritToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            usersSid, FileSystemRights.ReadAndExecute, inheritToChildren, PropagationFlags.None, AccessControlType.Allow));

        return security;
    }

    internal static void ValidateNoReparsePoints(string path) => EnsureNoReparsePoints(path);

    internal static void VerifyProtectedDirectorySecurity(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!security.AreAccessRulesProtected ||
            !administratorsSid.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("The elevated native library cache must have a protected Administrators-owned ACL.");
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inheritToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        bool HasRule(SecurityIdentifier sid, FileSystemRights rights) => rules.Any(rule =>
            sid.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights == rights &&
            rule.InheritanceFlags == inheritToChildren &&
            rule.PropagationFlags == PropagationFlags.None);

        if (rules.Length != 3 ||
            !HasRule(systemSid, FileSystemRights.FullControl) ||
            !HasRule(administratorsSid, FileSystemRights.FullControl) ||
            !HasRule(usersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize))
        {
            throw new UnauthorizedAccessException("The elevated native library cache ACL grants unexpected access.");
        }
    }

    internal static void VerifyProtectedCacheObjectSecurity(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!administratorsSid.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("Elevated cache objects must be owned by BUILTIN\\Administrators.");

        var currentIdentity = WindowsIdentity.GetCurrent();
        var writablePrincipals = new HashSet<string>(StringComparer.Ordinal)
        {
            currentIdentity.User?.Value ?? string.Empty,
        };
        if (currentIdentity.Groups != null)
        {
            foreach (IdentityReference group in currentIdentity.Groups)
            {
                if (!administratorsSid.Equals(group))
                    writablePrincipals.Add(group.Value);
            }
        }

        const FileSystemRights writeRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteData |
            FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                              writablePrincipals.Contains(rule.IdentityReference.Value) &&
                              (rule.FileSystemRights & writeRights) != 0))
        {
            throw new UnauthorizedAccessException("An elevated native library cache object is writable by the standard user token.");
        }
    }

    private static void LoadSdk(
        Assembly assembly,
        string name,
        string resourceName,
        string outputFileName,
        string versionPath,
        IList<string> possiblePaths
    )
    {
        Console.WriteLine($"[Loader] Preparing {name} SDK...");
        byte[] resourceBytes = ReadEmbeddedResource(assembly, resourceName);
        string dllPath = ExtractVerifiedResource(resourceBytes, versionPath, outputFileName);
        SetOnlyVerifiedSearchPath(possiblePaths, dllPath);
        Console.WriteLine($"[Loader] {name} SDK path verified: {dllPath}");
    }

    private static byte[] ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Cannot find embedded resource: {resourceName}.");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static bool FileMatchesHash(string path, byte[] expectedHash)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] actualHash = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // A concurrent atomic replacement can briefly deny shared read access.
            return false;
        }
    }

    private static bool WaitForExpectedCacheFile(string path, byte[] expectedHash)
    {
        const int attempts = 25;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (FileMatchesHash(path, expectedHash))
                return true;
            Thread.Sleep(10);
        }

        return false;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        DirectoryInfo? current = new DirectoryInfo(fullPath);
        while (current != null)
        {
            if (!current.Exists)
                throw new DirectoryNotFoundException($"Native library cache directory does not exist: {current.FullName}");
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Native library cache path contains a reparse point: {current.FullName}");
            current = current.Parent;
        }
    }
}
