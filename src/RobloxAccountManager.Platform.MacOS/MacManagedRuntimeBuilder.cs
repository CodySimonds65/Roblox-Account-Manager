using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace RobloxAccountManager.Platform.MacOS;

public sealed class MacManagedRuntimeBuilder
{
    public const int CurrentRuntimeRevision = 2;

    private readonly MacBundleDiscovery _bundleDiscovery;
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly MacSignatureVerifier _signatureVerifier;
    private readonly IRobloxProcessLocator _processLocator;

    public MacManagedRuntimeBuilder(
        string? runtimeRoot = null,
        MacBundleDiscovery? bundleDiscovery = null,
        IMacProcessCommandRunner? commandRunner = null,
        IRobloxProcessLocator? processLocator = null)
    {
        RuntimeRoot = Path.GetFullPath(runtimeRoot ?? GetDefaultRuntimeRoot());
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _signatureVerifier = new MacSignatureVerifier(_commandRunner);
        _bundleDiscovery = bundleDiscovery ?? new MacBundleDiscovery(
            _commandRunner,
            _signatureVerifier);
        _processLocator = processLocator ?? new MacRobloxProcessLocator();
    }

    public string RuntimeRoot { get; }

    public static string GetDefaultRuntimeRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "RobloxAccountManager", "RobloxRuntime");
    }

    public async Task<MacManagedRuntimeBuildResult> BuildAsync(
        MacManagedRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MacBundleInfo? source = await _bundleDiscovery.ValidateAsync(request.SourceBundlePath, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return new MacManagedRuntimeBuildResult(
                MacRuntimeBuildStatus.InvalidSource,
                null,
                null,
                null,
                "The selected bundle did not pass Roblox identifier, executable, location, and signature validation.");
        }

        var safeName = SanitizeRuntimeName(request.RuntimeName);
        PathSafety.EnsureOwnerOnlyDirectory(RuntimeRoot);
        var runtimePath = PathSafety.RequireContainedPath(RuntimeRoot, Path.Combine(RuntimeRoot, safeName));
        var stampPath = GetStampPath(runtimePath);
        var managedBundlePath = PathSafety.RequireContainedPath(
            runtimePath,
            Path.Combine(runtimePath, Path.GetFileName(source.BundlePath)),
            allowRoot: true);
        if (Directory.Exists(runtimePath)
            && !request.ForceRebuild
            && await ReadStampAsync(stampPath, cancellationToken).ConfigureAwait(false) is { } current
            && current.BuilderRevision == CurrentRuntimeRevision
            && string.Equals(current.SourceFingerprint, source.SourceFingerprint, StringComparison.Ordinal)
            && current.Level == request.Level
            && await _bundleDiscovery.ValidateManagedMultiInstanceRuntimeAsync(managedBundlePath, cancellationToken).ConfigureAwait(false) is { } verifiedRuntime
            && verifiedRuntime.SignatureVerified)
        {
            return new MacManagedRuntimeBuildResult(MacRuntimeBuildStatus.Reused, runtimePath, source, source.SourceFingerprint, null);
        }

        if (IsRuntimeBusy(runtimePath))
        {
            return new MacManagedRuntimeBuildResult(
                MacRuntimeBuildStatus.Busy,
                runtimePath,
                source,
                source.SourceFingerprint,
                "The managed runtime is still used by a verified Roblox process and cannot be rebuilt.");
        }

        var stage = PathSafety.RequireContainedPath(
            RuntimeRoot,
            Path.Combine(RuntimeRoot, $".staging-{Guid.NewGuid():N}"));
        var sidecarStage = PathSafety.RequireContainedPath(
            RuntimeRoot,
            Path.Combine(RuntimeRoot, $".stamp-{Guid.NewGuid():N}.json"));
        string? entitlementPath = null;
        try
        {
            Directory.CreateDirectory(stage);
            await CopyBundleAsync(source.BundlePath, stage, cancellationToken).ConfigureAwait(false);
            var stagedBundle = PathSafety.RequireContainedPath(stage, Path.Combine(stage, Path.GetFileName(source.BundlePath)), allowRoot: true);
            if (!Directory.Exists(stagedBundle))
            {
                throw new IOException("The staged Roblox bundle was not created.");
            }

            await PatchMinimalPlistAsync(stagedBundle, cancellationToken).ConfigureAwait(false);
            entitlementPath = await PrepareEntitlementsAsync(source.BundlePath, stage, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsMacOS())
            {
                await SignBundleAsync(stagedBundle, source.BundlePath, source.ExecutablePath, entitlementPath, cancellationToken).ConfigureAwait(false);
                if (await _bundleDiscovery.ValidateManagedMultiInstanceRuntimeAsync(
                        stagedBundle,
                        cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new InvalidOperationException("The managed Roblox runtime failed signature or multi-instance validation.");
                }
            }

            var stamp = new MacRuntimeStamp(
                source.BundlePath,
                source.SourceFingerprint,
                source.Version ?? string.Empty,
                source.Build ?? string.Empty,
                source.ExecutableFingerprint,
                source.PlistFingerprint,
                DateTimeOffset.UtcNow,
                request.Level,
                CurrentRuntimeRevision);
            await WriteJsonAtomicAsync(sidecarStage, stamp, cancellationToken).ConfigureAwait(false);
            CommitAtomically(stage, runtimePath, sidecarStage, stampPath);
            return new MacManagedRuntimeBuildResult(
                MacRuntimeBuildStatus.Built,
                runtimePath,
                source,
                source.SourceFingerprint,
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new MacManagedRuntimeBuildResult(
                MacRuntimeBuildStatus.Failed,
                null,
                source,
                source.SourceFingerprint,
                ex.Message);
        }
        finally
        {
            if (entitlementPath is not null)
            {
                DeleteSafeFile(entitlementPath, RuntimeRoot);
            }

            DeleteSafeDirectory(stage, RuntimeRoot);
            DeleteSafeFile(sidecarStage, RuntimeRoot);
            DeleteSafeFile(sidecarStage + ".tmp", RuntimeRoot);
        }
    }

    public static string SanitizeRuntimeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A runtime name is required.", nameof(name));
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        var value = builder.ToString().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            throw new ArgumentException("The runtime name does not contain a safe path component.", nameof(name));
        }

        return value;
    }

    private static string GetStampPath(string runtimePath) => runtimePath + ".runtime.json";

    private bool IsRuntimeBusy(string runtimePath)
    {
        try
        {
            return _processLocator.CaptureSnapshot().Processes.Any(
                process => PathSafety.IsContainedBy(runtimePath, process.Identity.BundlePath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // If process inspection is unavailable, fail closed: never rebuild a runtime we
            // cannot prove is unused.
            return true;
        }
    }

    private async Task CopyBundleAsync(string sourceBundle, string stageDirectory, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            var clone = await _commandRunner.RunAsync(
                "/bin/cp",
                ["-c", "-a", "--", sourceBundle, stageDirectory],
                cancellationToken).ConfigureAwait(false);
            if (clone.Succeeded)
            {
                return;
            }

            // cp may leave a partial destination after a failed clone. Remove only that exact
            // stage child after rechecking containment; never let fallback copy merge into it.
            var partial = PathSafety.RequireContainedPath(stageDirectory, Path.Combine(stageDirectory, Path.GetFileName(sourceBundle)));
            DeleteSafeDirectory(partial, stageDirectory);

            var sourceBytes = DirectorySize(sourceBundle);
            var available = new DriveInfo(Path.GetPathRoot(stageDirectory) ?? Path.DirectorySeparatorChar.ToString()).AvailableFreeSpace;
            if (available < sourceBytes)
            {
                throw new IOException("There is not enough free space for a full Roblox runtime copy.");
            }

            var fullCopy = await _commandRunner.RunAsync(
                "/bin/cp",
                ["-a", "--", sourceBundle, stageDirectory],
                cancellationToken).ConfigureAwait(false);
            if (!fullCopy.Succeeded)
            {
                throw new IOException("The APFS clone and full-copy fallback both failed.");
            }

            return;
        }

        CopyDirectory(sourceBundle, Path.Combine(stageDirectory, Path.GetFileName(sourceBundle)));
    }

    private async Task PatchMinimalPlistAsync(string stagedBundle, CancellationToken cancellationToken)
    {
        var plistPath = PathSafety.RequireContainedPath(stagedBundle, Path.Combine(stagedBundle, "Contents", "Info.plist"));
        if (OperatingSystem.IsMacOS())
        {
            var result = await _commandRunner.RunAsync(
                "/usr/bin/plutil",
                ["-replace", "LSMultipleInstancesProhibited", "-bool", "false", "--", plistPath],
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                result = await _commandRunner.RunAsync(
                    "/usr/bin/plutil",
                    ["-insert", "LSMultipleInstancesProhibited", "-bool", "false", "--", plistPath],
                    cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    throw new IOException("Unable to patch the managed runtime Info.plist.");
            }

            return;
        }

        var document = XDocument.Load(plistPath, LoadOptions.PreserveWhitespace);
        var dictionary = document.Descendants("dict").FirstOrDefault()
            ?? throw new InvalidOperationException("Info.plist did not contain a dictionary.");
        var keys = dictionary.Elements().ToList();
        for (var index = 0; index < keys.Count; index++)
        {
            if (keys[index].Name.LocalName == "key"
                && string.Equals(keys[index].Value, "LSMultipleInstancesProhibited", StringComparison.Ordinal))
            {
                if (index + 1 < keys.Count)
                {
                    keys[index + 1].Remove();
                }

                keys[index].Remove();
                break;
            }
        }

        dictionary.Add(new XElement("key", "LSMultipleInstancesProhibited"), new XElement("false"));
        document.Save(plistPath, SaveOptions.DisableFormatting);
    }

    private async Task<string?> PrepareEntitlementsAsync(
        string sourceBundle,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var extracted = await _signatureVerifier.ExtractEntitlementsAsync(sourceBundle, cancellationToken).ConfigureAwait(false);
        var entitlementXml = ExtractEntitlementXml(extracted);
        if (!extracted.Succeeded || entitlementXml is null)
        {
            throw new InvalidOperationException("Unable to extract source Roblox entitlements.");
        }

        var entitlementPath = PathSafety.RequireContainedPath(
            RuntimeRoot,
            Path.Combine(RuntimeRoot, $".entitlements-{Guid.NewGuid():N}.plist"));
        await File.WriteAllTextAsync(entitlementPath, entitlementXml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await EnsureDisableLibraryValidationAsync(entitlementPath, cancellationToken).ConfigureAwait(false);

        return entitlementPath;
    }

    private async Task SignBundleAsync(
        string stagedBundle,
        string sourceBundle,
        string sourceExecutablePath,
        string? entitlementPath,
        CancellationToken cancellationToken)
    {
        var mainExecutable = PathSafety.RequireContainedPath(
            stagedBundle,
            Path.Combine(stagedBundle, Path.GetRelativePath(sourceBundle, sourceExecutablePath)));
        var codeObjects = MacCodeObjectDiscovery.Enumerate(stagedBundle)
            .Concat([new MacCodeObject(mainExecutable, MacMachOFileType.Executable, IsBundle: false)])
            .DistinctBy(item => item.Path, StringComparer.Ordinal)
            .OrderByDescending(item => item.Path.Count(character =>
                character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar))
            .ThenBy(item => item.IsBundle)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToList();
        foreach (var codeObject in codeObjects)
        {
            var codePath = codeObject.Path;
            if (!PathSafety.IsContainedBy(stagedBundle, codePath))
            {
                throw new InvalidOperationException("A nested code path escaped the staged bundle.");
            }

            var isMainExecutable = PathSafety.PathsEqual(codePath, mainExecutable);
            var needsRuntimeEntitlements = isMainExecutable
                || codeObject.IsExecutable
                || IsExecutableCodeBundle(codePath);
            var nestedEntitlementPath = isMainExecutable
                ? entitlementPath
                : await ExtractNestedEntitlementsAsync(
                    sourceBundle,
                    Path.GetRelativePath(stagedBundle, codePath),
                    needsRuntimeEntitlements,
                    cancellationToken).ConfigureAwait(false);
            var args = new List<string> { "--force", "--sign", "-" };
            if (needsRuntimeEntitlements)
            {
                args.Add("--options");
                args.Add("runtime");
            }
            if (nestedEntitlementPath is not null)
            {
                args.Add("--entitlements");
                args.Add(nestedEntitlementPath);
            }

            args.Add("--");
            args.Add(codePath);
            try
            {
                var result = await _commandRunner.RunAsync("/usr/bin/codesign", args, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException("A nested Roblox code-signing stage failed.");
                }
            }
            finally
            {
                if (!isMainExecutable && nestedEntitlementPath is not null)
                {
                    DeleteSafeFile(nestedEntitlementPath, RuntimeRoot);
                }
            }
        }

        // Preserve hardened-runtime behavior on the locally signed clone. The
        // disable-library-validation entitlement is only meaningful when the
        // hardened runtime remains enabled on the main bundle signature.
        var outerArgs = new List<string> { "--force", "--sign", "-", "--options", "runtime" };
        if (entitlementPath is not null)
        {
            outerArgs.Add("--entitlements");
            outerArgs.Add(entitlementPath);
        }

        outerArgs.Add("--");
        outerArgs.Add(stagedBundle);
        var outer = await _commandRunner.RunAsync("/usr/bin/codesign", outerArgs, cancellationToken).ConfigureAwait(false);
        if (!outer.Succeeded)
        {
            throw new InvalidOperationException("The outer Roblox bundle code-signing stage failed.");
        }
    }

    private async Task<string?> ExtractNestedEntitlementsAsync(
        string sourceBundle,
        string relativePath,
        bool ensureDisableLibraryValidation,
        CancellationToken cancellationToken)
    {
        var sourcePath = PathSafety.RequireContainedPath(sourceBundle, Path.Combine(sourceBundle, relativePath));
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return null;
        }

        var extracted = await _signatureVerifier.ExtractEntitlementsAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var entitlementXml = ExtractEntitlementXml(extracted);
        if (!extracted.Succeeded && !ensureDisableLibraryValidation)
        {
            return null;
        }

        entitlementXml ??= ensureDisableLibraryValidation
            ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict/></plist>"
            : null;
        if (entitlementXml is null)
            return null;

        var entitlementPath = PathSafety.RequireContainedPath(
            RuntimeRoot,
            Path.Combine(RuntimeRoot, $".nested-entitlements-{Guid.NewGuid():N}.plist"));
        await File.WriteAllTextAsync(entitlementPath, entitlementXml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (ensureDisableLibraryValidation)
            await EnsureDisableLibraryValidationAsync(entitlementPath, cancellationToken).ConfigureAwait(false);
        return entitlementPath;
    }

    private async Task EnsureDisableLibraryValidationAsync(
        string entitlementPath,
        CancellationToken cancellationToken)
    {
        var patch = await _commandRunner.RunAsync(
            "/usr/bin/plutil",
            ["-replace", "com\\.apple\\.security\\.cs\\.disable-library-validation", "-bool", "true", "--", entitlementPath],
            cancellationToken).ConfigureAwait(false);
        if (patch.Succeeded)
            return;

        patch = await _commandRunner.RunAsync(
            "/usr/bin/plutil",
            ["-insert", "com\\.apple\\.security\\.cs\\.disable-library-validation", "-bool", "true", "--", entitlementPath],
            cancellationToken).ConfigureAwait(false);
        if (!patch.Succeeded)
            throw new InvalidOperationException("Unable to prepare managed-runtime entitlements.");
    }

    private static bool IsExecutableCodeBundle(string path) =>
        path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".appex", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".xpc", StringComparison.OrdinalIgnoreCase);

    private void CommitAtomically(string stage, string destination, string stagedStamp, string destinationStamp)
    {
        PathSafety.RequireContainedPath(RuntimeRoot, stage);
        PathSafety.RequireContainedPath(RuntimeRoot, destination);
        PathSafety.RequireContainedPath(RuntimeRoot, stagedStamp);
        PathSafety.RequireContainedPath(RuntimeRoot, destinationStamp);
        PathSafety.RejectSymlinkDirectory(RuntimeRoot);

        var oldPath = PathSafety.RequireContainedPath(RuntimeRoot, Path.Combine(RuntimeRoot, $".old-{Guid.NewGuid():N}"));
        var oldStamp = PathSafety.RequireContainedPath(RuntimeRoot, Path.Combine(RuntimeRoot, $".old-stamp-{Guid.NewGuid():N}.json"));
        var movedOld = false;
        var movedNew = false;
        try
        {
            if (Directory.Exists(destination))
            {
                if (IsRuntimeBusy(destination))
                {
                    throw new IOException("The managed runtime became busy during the atomic rebuild.");
                }

                PathSafety.RejectSymlinkDirectory(destination);
                Directory.Move(destination, oldPath);
                movedOld = true;
            }

            PathSafety.RejectSymlinkComponents(RuntimeRoot);
            if (Directory.Exists(destination))
            {
                throw new IOException("The destination changed while preparing an atomic runtime replacement.");
            }

            Directory.Move(stage, destination);
            movedNew = true;
            if (File.Exists(destinationStamp))
            {
                PathSafety.RejectSymlink(destinationStamp);
                File.Move(destinationStamp, oldStamp);
            }

            File.Move(stagedStamp, destinationStamp);
            DeleteSafeDirectory(oldPath, RuntimeRoot);
            DeleteSafeFile(oldStamp, RuntimeRoot);
        }
        catch
        {
            if (movedNew && !IsRuntimeBusy(destination))
            {
                DeleteSafeDirectory(destination, RuntimeRoot);
            }
            if (movedOld && Directory.Exists(oldPath) && !Directory.Exists(destination))
            {
                Directory.Move(oldPath, destination);
            }

            if (File.Exists(oldStamp) && !File.Exists(destinationStamp))
            {
                File.Move(oldStamp, destinationStamp);
            }

            throw;
        }
    }

    private static async Task<MacRuntimeStamp?> ReadStampAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<MacRuntimeStamp>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? ExtractEntitlementXml(MacProcessCommandResult result)
    {
        var combined = result.StandardOutput + "\n" + result.StandardError;
        var start = combined.IndexOf("<plist", StringComparison.OrdinalIgnoreCase);
        var end = combined.IndexOf("</plist>", start >= 0 ? start : 0, StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end >= start
            ? combined[start..(end + "</plist>".Length)]
            : null;
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path);
    }

    private static long DirectorySize(string path)
    {
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static void CopyDirectory(string source, string destination)
    {
        PathSafety.RejectSymlinkDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            PathSafety.RejectSymlinkDirectory(directory);
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            PathSafety.RejectSymlink(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
    }

    private static void DeleteSafeDirectory(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        if (!PathSafety.IsContainedBy(root, path) || PathSafety.PathsEqual(root, path))
        {
            return;
        }

        PathSafety.RejectSymlinkDirectory(path);
        Directory.Delete(path, recursive: true);
    }

    private static void DeleteSafeFile(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !PathSafety.IsContainedBy(root, path))
        {
            return;
        }

        PathSafety.RejectSymlink(path);
        File.Delete(path);
    }
}
