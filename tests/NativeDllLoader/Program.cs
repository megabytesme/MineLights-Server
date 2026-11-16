using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Reflection;
using RGB.NET.Devices.Corsair;
using RGB.NET.Devices.Logitech;
using RGB.NET.Devices.Msi;
using RGB.NET.Devices.Wooting;

if (args.Length == 2 && args[0] == "--cleanup-test-fixture")
{
    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string target = Path.GetFullPath(args[1]);
    if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fixture cleanup is restricted to the system temp directory.");
    EnsureTreeHasNoReparsePoints(target);
    Directory.Delete(target, recursive: true);
    Console.WriteLine("Temporary ACL fixture removed.");
    return;
}

if (args.Length == 2 && args[0] == "--prepare-protected-cache-root")
{
    Console.WriteLine(NativeDllLoader.PrepareProtectedCacheRoot(args[1]));
    return;
}

if (args.Length == 2 && args[0] == "--populate-protected-cache")
{
    string root = NativeDllLoader.PrepareProtectedCacheRoot(args[1]);
    Assembly assembly = typeof(NativeDllLoader).Assembly;
    string version = assembly.GetName().Version!.ToString();
    string versionPath = Path.Combine(root, "x64", version);
    Directory.CreateDirectory(versionPath);
    NativeDllLoader.ValidateNoReparsePoints(versionPath);

    foreach ((string resourceName, string fileName) in new[]
    {
        ("MineLights.resources.x64.iCUESDK.x64_2019.dll", "iCUESDK.x64_2019.dll"),
        ("MineLights.resources.x64.MysticLight_SDK_x64.dll", "MysticLight_SDK_x64.dll"),
        ("MineLights.resources.x64.LogitechLedEnginesWrapper.dll", "LogitechLedEnginesWrapper.dll"),
        ("MineLights.resources.x64.wooting-rgb-sdk.dll", "wooting-rgb-sdk.dll"),
    })
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded DLL not found: {resourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Console.WriteLine(NativeDllLoader.ExtractVerifiedResource(memory.ToArray(), versionPath, fileName));
    }

    return;
}

if (args.Length == 2 && args[0] == "--configure-protected-native-paths")
{
    string version = typeof(NativeDllLoader).Assembly.GetName().Version!.ToString();
    NativeDllLoader.ConfigureNativeSdkPaths(Path.Combine(args[1], "x64", version));
    AssertProviderPath(CorsairDeviceProvider.PossibleX64NativePaths, "iCUESDK.x64_2019.dll");
    AssertProviderPath(MsiDeviceProvider.PossibleX64NativePaths, "MysticLight_SDK_x64.dll");
    AssertProviderPath(LogitechDeviceProvider.PossibleX64NativePaths, "LogitechLedEnginesWrapper.dll");
    AssertProviderPath(WootingDeviceProvider.PossibleX64NativePathsWindows, "wooting-rgb-sdk.dll");
    Console.WriteLine("All RGB.NET providers use only their verified absolute DLL path.");
    return;
}

if (args.Length == 2 && args[0] == "--assert-no-reparse")
{
    NativeDllLoader.ValidateNoReparsePoints(args[1]);
    Console.WriteLine("Cache ancestry contains no reparse points.");
    return;
}

if (args.Length == 2 && args[0] == "--verify-protected-cache-acl")
{
    var directory = new DirectoryInfo(args[1]);
    NativeDllLoader.VerifyProtectedDirectorySecurity(directory.GetAccessControl(
        AccessControlSections.Access | AccessControlSections.Owner));
    foreach (string childDirectory in Directory.EnumerateDirectories(directory.FullName, "*", SearchOption.AllDirectories))
        NativeDllLoader.VerifyProtectedCacheObjectSecurity(new DirectoryInfo(childDirectory).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    foreach (string childFile in Directory.EnumerateFiles(directory.FullName, "*", SearchOption.AllDirectories))
        NativeDllLoader.VerifyProtectedCacheObjectSecurity(new FileInfo(childFile).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    Console.WriteLine("Protected cache ACL verified.");
    return;
}

if (args.Length == 2 && args[0] == "--assert-cache-write-denied")
{
    AssertDenied(() => File.WriteAllText(Path.Combine(args[1], $"write-probe-{Guid.NewGuid():N}.tmp"), "unexpected write"));
    string? cachedFile = Directory.EnumerateFiles(args[1], "*.dll", SearchOption.AllDirectories).FirstOrDefault();
    if (cachedFile == null)
        throw new InvalidOperationException("The protected cache has no DLL file to test.");
    AssertDenied(() => File.WriteAllBytes(cachedFile, [99, 98, 97]));
    AssertDenied(() => File.Delete(cachedFile));
    var acl = new DirectoryInfo(args[1]).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
    SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User!;
    acl.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    AssertDenied(() => new DirectoryInfo(args[1]).SetAccessControl(acl));
    Console.WriteLine("Standard user could not create, modify, delete, or change ACLs in the protected cache.");
    return;
}

if (args.Length == 2 && args[0] == "--assert-reparse-rejected")
{
    try
    {
        NativeDllLoader.PrepareProtectedCacheRoot(args[1]);
    }
    catch (IOException)
    {
        Console.WriteLine("Reparse-point cache parent rejected.");
        return;
    }

    throw new InvalidOperationException("The protected cache accepted a reparse-point parent.");
}

var fixtureRoot = Path.Combine(Path.GetTempPath(), "MineLights.NativeDllLoader.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var testRoot = fixtureRoot;

try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        testRoot = NativeDllLoader.PrepareProtectedCacheRoot(fixtureRoot);
    VerifyContentAddressedExtraction();
    VerifyUnexpectedCachedDllIsReplaced();
    VerifyVersionsUseSeparateDirectories();
    VerifyConcurrentExtraction();
    VerifySearchPathExcludesFallbacks();
    VerifyUnsafePathsAreRejected();
    VerifyProtectedAclDescriptor();
    Console.WriteLine("All NativeDllLoader checks passed.");
}
finally
{
    Directory.Delete(fixtureRoot, recursive: true);
}

void VerifyContentAddressedExtraction()
{
    byte[] expected = [1, 2, 3, 4, 5];
    string versionPath = Path.Combine(testRoot, "2.4.0.0");

    string path = NativeDllLoader.ExtractVerifiedResource(expected, versionPath, "sdk.dll");

    string expectedHash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
    Assert(Path.GetFileName(Path.GetDirectoryName(path)) == expectedHash, "DLL is not stored under its SHA-256 hash.");
    Assert(Path.GetFullPath(path) == path, "Extracted DLL path is not absolute.");
    Assert(File.ReadAllBytes(path).SequenceEqual(expected), "Extracted DLL bytes differ from the embedded resource.");
}

void VerifyUnexpectedCachedDllIsReplaced()
{
    byte[] expected = [11, 12, 13];
    string versionPath = Path.Combine(testRoot, "2.4.0.0");
    string path = NativeDllLoader.ExtractVerifiedResource(expected, versionPath, "sdk.dll");
    File.WriteAllBytes(path, [99, 98, 97]);

    string repairedPath = NativeDllLoader.ExtractVerifiedResource(expected, versionPath, "sdk.dll");

    Assert(path == repairedPath, "A corrupt cache entry unexpectedly changed its content-addressed path.");
    Assert(File.ReadAllBytes(repairedPath).SequenceEqual(expected), "A corrupt cached DLL was not replaced.");
}

void VerifyVersionsUseSeparateDirectories()
{
    byte[] versionOne = [21, 22, 23];
    byte[] versionTwo = [31, 32, 33];
    string first = NativeDllLoader.ExtractVerifiedResource(versionOne, Path.Combine(testRoot, "2.4.0.0"), "sdk.dll");
    string second = NativeDllLoader.ExtractVerifiedResource(versionTwo, Path.Combine(testRoot, "2.4.1.0"), "sdk.dll");

    Assert(first != second, "Different server versions share a native DLL path.");
    Assert(File.ReadAllBytes(first).SequenceEqual(versionOne), "The first version's DLL changed.");
    Assert(File.ReadAllBytes(second).SequenceEqual(versionTwo), "The second version's DLL changed.");
}

void VerifyConcurrentExtraction()
{
    byte[] expected = Enumerable.Range(0, 64_000).Select(value => (byte)(value % 251)).ToArray();
    string versionPath = Path.Combine(testRoot, "2.4.2.0");

    string[] paths = Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => NativeDllLoader.ExtractVerifiedResource(expected, versionPath, "sdk.dll"))))
        .GetAwaiter()
        .GetResult();

    Assert(paths.All(path => path == paths[0]), "Concurrent extraction returned multiple paths.");
    Assert(File.ReadAllBytes(paths[0]).SequenceEqual(expected), "Concurrent extraction left invalid DLL contents.");
    Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(paths[0])!, "*.tmp").Any(), "Concurrent extraction left staging files.");
}

void VerifySearchPathExcludesFallbacks()
{
    var paths = new List<string> { "x64/stale.dll", "another/fallback.dll" };
    string verifiedPath = Path.Combine(testRoot, "verified", "sdk.dll");

    NativeDllLoader.SetOnlyVerifiedSearchPath(paths, verifiedPath);
    NativeDllLoader.SetOnlyVerifiedSearchPath(paths, verifiedPath);

    Assert(paths.Count == 1 && paths[0] == verifiedPath, "RGB.NET search paths still contain unverified fallbacks.");
}

void VerifyUnsafePathsAreRejected()
{
    AssertThrows<ArgumentException>(
        () => NativeDllLoader.ExtractVerifiedResource([1], Path.Combine(testRoot, "paths"), "..\\outside.dll"),
        "A path-bearing native library name was accepted.");
    AssertThrows<ArgumentException>(
        () => NativeDllLoader.SetOnlyVerifiedSearchPath([], "relative.dll"),
        "A relative native library path was accepted.");
}

void VerifyProtectedAclDescriptor()
{
    var security = NativeDllLoader.CreateProtectedDirectorySecurity();
    NativeDllLoader.VerifyProtectedDirectorySecurity(security);
}

void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

void AssertDenied(Action action)
{
    try
    {
        action();
    }
    catch (UnauthorizedAccessException)
    {
        return;
    }
    catch (IOException)
    {
        return;
    }

    throw new InvalidOperationException("The standard user token could modify the protected cache.");
}

void AssertProviderPath(IList<string> paths, string expectedFileName)
{
    if (paths.Count != 1 || !Path.IsPathFullyQualified(paths[0]) ||
        Path.GetFileName(paths[0]) != expectedFileName || !File.Exists(paths[0]))
    {
        throw new InvalidOperationException($"RGB.NET provider path is not the verified {expectedFileName}.");
    }
}

void EnsureTreeHasNoReparsePoints(string root)
{
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.TryPop(out string? current))
    {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Refusing cleanup through reparse point: {current}");
        if ((File.GetAttributes(current) & FileAttributes.Directory) == 0)
            continue;
        foreach (string child in Directory.EnumerateFileSystemEntries(current))
            pending.Push(child);
    }
}

void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
