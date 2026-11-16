using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("The local application data directory is unavailable.");

            string version = assembly.GetName().Version?.ToString()
                ?? throw new InvalidOperationException("The server assembly version is unavailable.");
            string versionPath = Path.Combine(localAppData, "MineLights", "NativeLibs", "x64", version);
            Console.WriteLine($"[Loader] Using versioned native library path: {versionPath}");

            LoadSdk(
                assembly,
                "Corsair",
                "MineLights.resources.x64.iCUESDK.x64_2019.dll",
                "iCUESDK.x64_2019.dll",
                versionPath,
                CorsairDeviceProvider.PossibleX64NativePaths
            );
            LoadSdk(
                assembly,
                "MSI",
                "MineLights.resources.x64.MysticLight_SDK_x64.dll",
                "MysticLight_SDK_x64.dll",
                versionPath,
                MsiDeviceProvider.PossibleX64NativePaths
            );
            LoadSdk(
                assembly,
                "Logitech",
                "MineLights.resources.x64.LogitechLedEnginesWrapper.dll",
                "LogitechLedEnginesWrapper.dll",
                versionPath,
                LogitechDeviceProvider.PossibleX64NativePaths
            );
            LoadSdk(
                assembly,
                "Wooting",
                "MineLights.resources.x64.wooting-rgb-sdk.dll",
                "wooting-rgb-sdk.dll",
                versionPath,
                WootingDeviceProvider.PossibleX64NativePathsWindows
            );
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

        string outputPath = Path.Combine(outputDirectory, fileName);
        if (FileMatchesHash(outputPath, expectedHash))
            return outputPath;

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
                    && FileMatchesHash(outputPath, expectedHash))
            {
                // Another process completed the same content-addressed extraction first.
            }

            if (!FileMatchesHash(outputPath, expectedHash))
                throw new IOException($"Extracted native library '{fileName}' does not match its embedded resource.");

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
    }
}
