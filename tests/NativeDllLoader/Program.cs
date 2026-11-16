using System.Security.Cryptography;

var testRoot = Path.Combine(Path.GetTempPath(), "MineLights.NativeDllLoader.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    VerifyContentAddressedExtraction();
    VerifyUnexpectedCachedDllIsReplaced();
    VerifyVersionsUseSeparateDirectories();
    VerifyConcurrentExtraction();
    VerifySearchPathExcludesFallbacks();
    VerifyUnsafePathsAreRejected();
    Console.WriteLine("All NativeDllLoader checks passed.");
}
finally
{
    Directory.Delete(testRoot, recursive: true);
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

void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
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
