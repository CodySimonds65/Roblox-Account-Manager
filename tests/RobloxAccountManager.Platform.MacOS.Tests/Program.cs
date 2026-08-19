using RobloxAccountManager.Platform.MacOS;
using Contracts = RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;
using System.Security.Cryptography;

var passed = 0;
var skipped = 0;

void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }

    passed++;
}

void Skip(string message)
{
    Console.WriteLine($"SKIP: {message}");
    skipped++;
}

// sem_unlink's native call is not made off-host; its return/errno mapping is pure and exhaustive.
Check(MacSemaphoreMapping.Map(0, 0).Status == SingletonReleaseStatus.Removed,
    "sem_unlink(0) was not mapped to Removed.");
var absent = MacSemaphoreMapping.Map(-1, 2);
Check(absent.Status == SingletonReleaseStatus.AlreadyAbsent && absent.ErrorName == "ENOENT",
    "ENOENT was not mapped to AlreadyAbsent.");
var denied = MacSemaphoreMapping.Map(-1, 13);
Check(denied.Status == SingletonReleaseStatus.Failed && denied.ErrorName == "EACCES" && denied.NativeError == 13,
    "A non-ENOENT errno was not retained as a failure.");

var hello = new MacPluginHello(
    "auth-ticket-token",
    "sample.plugin",
    Environment.ProcessId,
    DateTimeOffset.UtcNow,
    new string('a', 64),
    ["window-focus"]);
Check(!hello.ToString().Contains("auth-ticket-token", StringComparison.Ordinal),
    "Plugin hello ToString leaked the authentication token.");
var commandResult = new MacProcessCommandResult(0, "auth-ticket-token", "auth-ticket-token");
Check(!commandResult.ToString().Contains("auth-ticket-token", StringComparison.Ordinal),
    "Process command diagnostics leaked sensitive command output.");

var unconfiguredDiscovery = new MacBundleDiscovery(requiredTeamIdentifier: null);
Check(!unconfiguredDiscovery.HasTrustedTeamIdentifier,
    "Bundle discovery accepted a missing trusted Team ID configuration.");
var rejectedLauncher = false;
try
{
    _ = new MacCorePlatformLauncher(unconfiguredDiscovery);
}
catch (ArgumentException exception) when (exception.Message.Contains("trusted-source-team-id-required", StringComparison.Ordinal))
{
    rejectedLauncher = true;
}
Check(rejectedLauncher, "The platform launcher accepted an unconfigured bundle validator.");
var configuredDiscovery = new MacBundleDiscovery(requiredTeamIdentifier: "TEAM123456");
Check(configuredDiscovery.HasTrustedTeamIdentifier,
    "Bundle discovery did not retain an explicitly configured trusted Team ID.");

var tempRoot = Path.Combine(Path.GetTempPath(), "ram-mac-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var child = Path.Combine(tempRoot, "child", "runtime");
    var sibling = Path.Combine(tempRoot, "..", Path.GetFileName(tempRoot) + "-escape");
    Check(PathSafety.IsContainedBy(tempRoot, child), "A child path failed containment.");
    Check(!PathSafety.IsContainedBy(tempRoot, sibling), "A sibling prefix passed containment.");

    var upperRoot = OperatingSystem.IsWindows() ? tempRoot.ToUpperInvariant() : tempRoot.ToUpperInvariant();
    var pathsEqual = PathSafety.PathsEqual(tempRoot, upperRoot);
    Check(pathsEqual == OperatingSystem.IsWindows(),
        "Path case comparison did not follow the platform safety policy.");

    var outside = Path.Combine(Path.GetTempPath(), "ram-mac-link-target-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var link = Path.Combine(tempRoot, "link");
    try
    {
        Directory.CreateSymbolicLink(link, outside);
        var rejected = false;
        try
        {
            PathSafety.RejectSymlinkComponents(link);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected, "A symlink path component was not rejected.");
    }
    catch (UnauthorizedAccessException)
    {
        Skip("Symbolic-link creation is unavailable in this host token.");
    }
    catch (IOException)
    {
        Skip("Symbolic-link creation is unavailable in this filesystem.");
    }
    finally
    {
        if (File.Exists(link) || Directory.Exists(link))
        {
            try { Directory.Delete(link); } catch { }
        }

        if (Directory.Exists(outside))
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    var registryPath = Path.Combine(tempRoot, "registry", "managed.json");
    var registry = new MacManagedProcessRegistry(registryPath);
    var identity = new RobloxProcessIdentity(
        12345,
        DateTimeOffset.UtcNow,
        Path.Combine(tempRoot, "Roblox.app", "Contents", "MacOS", "Roblox"),
        Path.Combine(tempRoot, "Roblox.app"));
    Check(!registry.IsRegistered(identity),
        "A discovered identity was treated as managed before explicit registration.");
    registry.Register(identity);
    Check(registry.IsRegistered(identity), "An explicitly registered identity was not retained.");
    registry.Unregister(identity);
    Check(!registry.IsRegistered(identity), "An unregistered identity remained managed.");

    var robloxSettingsPath = Path.Combine(tempRoot, "GlobalBasicSettings_13.xml");
    var robloxEnginePath = Path.Combine(tempRoot, "ClientAppSettings.json");
    await File.WriteAllTextAsync(robloxSettingsPath,
        "<Roblox><Item class=\"UserGameSettings\"><Properties>" +
        "<Item name=\"GraphicsQuality\"><int name=\"value\">3</int></Item>" +
        "<Item name=\"FramerateCap\"><int name=\"value\">60</int></Item>" +
        "</Properties></Item></Roblox>");
    await File.WriteAllTextAsync(robloxEnginePath, "{\"ExistingFlag\": true}");
    var settingsAdapter = new MacRobloxSettingsAdapter(robloxSettingsPath, robloxEnginePath);
    var settingsResult = await settingsAdapter.ApplyAsync(new GameSettings { GraphicsQuality = 7, FpsLimit = 120, TextureQuality = 2 });
    Check(settingsResult.Applied.Contains("graphics-quality") && settingsResult.Applied.Contains("fps") && settingsResult.Applied.Contains("engine-flags"),
        "The macOS Roblox settings adapter did not apply supported settings.");
    Check((await File.ReadAllTextAsync(robloxEnginePath)).Contains("DFIntTextureQualityOverride", StringComparison.Ordinal),
        "The macOS Roblox settings adapter did not persist engine flags atomically.");

    var partialSettingsPath = Path.Combine(tempRoot, "PartialGlobalBasicSettings_13.xml");
    var partialEnginePath = Path.Combine(tempRoot, "PartialClientAppSettings.json");
    await File.WriteAllTextAsync(partialSettingsPath,
        "<Roblox><Item class=\"UserGameSettings\"><Properties>" +
        "<Item name=\"GraphicsQuality\"><int name=\"value\">3</int></Item>" +
        "</Properties></Item></Roblox>");
    await File.WriteAllTextAsync(partialEnginePath, "{\"ExistingFlag\": true}");
    var partialAdapter = new MacRobloxSettingsAdapter(partialSettingsPath, partialEnginePath);
    var partialResult = await partialAdapter.ApplyAsync(new GameSettings { GraphicsQuality = 7, FpsLimit = 120, TextureQuality = 2 });
    Check(!partialResult.Succeeded && partialResult.Skipped.Count > 0,
        "The macOS settings adapter reported partial unsupported settings as successful.");
    Check(!(await File.ReadAllTextAsync(partialEnginePath)).Contains("DFIntTextureQualityOverride", StringComparison.Ordinal),
        "The macOS settings adapter committed engine flags after an unsupported scoped setting.");

    var pluginRoot = Path.Combine(tempRoot, "plugins");
    var pluginDirectory = Path.Combine(pluginRoot, "sample.plugin");
    Directory.CreateDirectory(pluginDirectory);
    await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "plugin.json"),
        "{\"schemaVersion\":2,\"id\":\"sample.plugin\",\"capabilities\":[\"host.accounts.read\"],\"entryPoints\":{\"osx-x64\":\"plugin\"}}");
    await File.WriteAllBytesAsync(Path.Combine(pluginDirectory, "plugin"), [1, 2, 3]);
    var pluginHost = new MacPluginHostFacade(pluginRoot);
    var pluginIds = await pluginHost.GetInstalledPluginIdsAsync();
    Check(pluginIds.Contains("sample.plugin", StringComparer.Ordinal),
        "A macOS RID-matched plugin was not discovered.");
    var unsupportedStart = await pluginHost.StartAsync("sample.plugin", userConfirmed: true);
    if (!OperatingSystem.IsMacOS())
        Check(!unsupportedStart.Succeeded && unsupportedStart.DiagnosticCode == "platform-not-supported",
            "The macOS plugin host attempted to start a plugin off-host.");
}
finally
{
    if (Directory.Exists(tempRoot))
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

var remover = new MacAccountBrowserDataStoreRemover();
if (!OperatingSystem.IsMacOSVersionAtLeast(14))
{
    Check(!remover.IsSupported, "WK data-store removal was advertised off macOS 14.");
    try
    {
        await remover.RemoveAsync(Guid.NewGuid());
        throw new InvalidOperationException("Unsupported WK data-store removal unexpectedly succeeded.");
    }
    catch (PlatformNotSupportedException exception)
    {
        Check(exception.Message.Contains("platform-not-supported", StringComparison.Ordinal),
            "Unsupported data-store removal did not expose a stable failure code.");
    }
}
else
{
    Skip("WKWebsiteDataStore removal is macOS-only and was not invoked by the off-host suite.");
}

var updateRoot = Path.Combine(Path.GetTempPath(), "ram-mac-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(updateRoot);
try
{
    var pkgPath = Path.Combine(updateRoot, "RAM Update With Spaces.pkg");
    var pkgBytes = RandomNumberGenerator.GetBytes(4096);
    await File.WriteAllBytesAsync(pkgPath, pkgBytes);
    var hash = Convert.ToHexString(SHA256.HashData(pkgBytes));
    var signedRunner = new RecordingCommandRunner(new MacProcessCommandResult(
        0,
        "Package " + pkgPath + "\nStatus: signed by a certificate trusted by Gatekeeper\nDeveloper ID Installer: Example (TEAM123)",
        string.Empty));
    var trust = new MacPkgTrustConfiguration(
        "Example (TEAM123)",
        "com.example.roblox.pkg",
        "io.github.codysimonds65.roblox-account-manager",
        "RobloxAccountManager");
    var installer = new MacPkgUpdateInstaller(signedRunner, expectedRid: "osx-arm64", trust, updateRoot, "2", "2.0");
    var validPackage = new Contracts.UpdatePackage(
        Contracts.RobloxPlatform.MacOS,
        "osx-arm64",
        new Version(3, 0),
        "3",
        new Uri("https://updates.example.test/RobloxAccountManager-osx-arm64.pkg"),
        hash,
        pkgPath);
    Check(await installer.ValidateAsync(validPackage) is null, "A valid signed PKG was rejected.");
    var prepared = await installer.PrepareVerifiedPackageAsync(validPackage);
    Check(prepared.Error is null && prepared.Path is not null
        && !string.Equals(Path.GetFullPath(prepared.Path), Path.GetFullPath(pkgPath), StringComparison.Ordinal),
        "The verified update was not copied to a distinct staged path before handoff.");
    Check(prepared.Path is not null && PathSafety.IsContainedBy(updateRoot, prepared.Path),
        "The verified update stage escaped the owner-controlled update directory.");
    var stagedArguments = MacPkgUpdateInstaller.BuildOpenArguments(validPackage with { LocalPath = prepared.Path! });
    Check(stagedArguments.Count == 4
        && stagedArguments[0] == "-a"
        && stagedArguments[1] == "/System/Library/CoreServices/Installer.app"
        && stagedArguments[2] == "--"
        && stagedArguments[3] == prepared.Path,
        "The installer handoff did not pin Apple Installer or use the exact canonical staged path.");
    if (prepared.Path is not null && File.Exists(prepared.Path))
    {
        File.Delete(prepared.Path);
    }

    Check(await installer.ValidateAsync(validPackage with { Rid = "osx-x64" }) == "rid-mismatch",
        "A package for the wrong macOS architecture was accepted.");
    Check(await installer.ValidateAsync(validPackage with { PackageUri = new Uri("http://updates.example.test/update.pkg") }) == "https-required",
        "A non-HTTPS update URL was accepted.");
    Check(await installer.ValidateAsync(validPackage with { Sha256 = new string('0', 64) }) == "sha256-mismatch",
        "A package with the wrong SHA-256 was accepted.");
    var wrongSigner = new MacPkgUpdateInstaller(
        signedRunner,
        expectedRid: "osx-arm64",
        trust with { ExpectedInstallerIdentity = "Other Installer" },
        updateRoot,
        "2",
        "2.0");
    Check(await wrongSigner.ValidateAsync(validPackage) == "installer-identity-mismatch",
        "A package with the wrong configured Developer ID Installer identity was accepted.");

    var wrongPackageId = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, packageIdentifier: "com.example.other.pkg"),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await wrongPackageId.ValidateAsync(validPackage) == "pkg-identity-mismatch",
        "A PKG with the wrong internal package identifier was accepted.");

    var wrongVersion = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, packageVersion: "2.9"),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await wrongVersion.ValidateAsync(validPackage) == "pkg-version-mismatch",
        "A PKG with the wrong internal version was accepted.");

    var wrongArchitecture = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, architecture: "x86_64"),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await wrongArchitecture.ValidateAsync(validPackage) == "pkg-architecture-mismatch",
        "A PKG with the wrong payload architecture was accepted.");

    var unexpectedPayload = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, includeUnexpectedPayload: true),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await unexpectedPayload.ValidateAsync(validPackage) == "pkg-payload-unexpected-path",
        "A PKG with an unexpected payload path was accepted.");

    var scriptedPayload = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, includeScripts: true),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await scriptedPayload.ValidateAsync(validPackage) == "pkg-installer-scripts-not-allowed",
        "A PKG with installer scripts was accepted.");

    var declaredScript = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, includeScriptDeclaration: true),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await declaredScript.ValidateAsync(validPackage) == "pkg-installer-scripts-not-allowed",
        "A PKG with a PackageInfo script declaration was accepted.");

    var unsigned = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(new MacProcessCommandResult(1, string.Empty, "signature invalid")),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await unsigned.ValidateAsync(validPackage) == "pkg-signature-invalid",
        "An invalid pkgutil signature was accepted.");

    var unsignedDevelopmentTrust = trust with { ExpectedInstallerIdentity = "unsigned-development", AllowUnsignedPackages = true };
    var unsignedDevelopment = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(new MacProcessCommandResult(1, string.Empty, "unsigned package")),
        expectedRid: "osx-arm64",
        unsignedDevelopmentTrust,
        updateRoot,
        "2",
        "2.0");
    Check(await unsignedDevelopment.ValidateAsync(validPackage with { IsUnsigned = true }) is null,
        "The explicitly labeled unsigned development PKG was not accepted after checksum and payload validation.");

    var signedInUnsignedMode = new MacPkgUpdateInstaller(
        signedRunner,
        expectedRid: "osx-arm64",
        unsignedDevelopmentTrust,
        updateRoot,
        "2",
        "2.0");
    Check(await signedInUnsignedMode.ValidateAsync(validPackage) == "installer-identity-mismatch",
        "Unsigned development mode disabled trusted signer identity validation.");

    var openArguments = MacPkgUpdateInstaller.BuildOpenArguments(validPackage);
    Check(openArguments.Count == 4
        && openArguments[0] == "-a"
        && openArguments[1] == "/System/Library/CoreServices/Installer.app"
        && openArguments[2] == "--"
        && openArguments[3] == pkgPath,
        "PKG launch arguments were not pinned to Apple Installer or passed safely.");
    Check(!string.Join(' ', openArguments).Contains("--apply-update", StringComparison.Ordinal),
        "The macOS PKG handoff unexpectedly exposed a custom privileged update mode.");
}
finally
{
    if (Directory.Exists(updateRoot))
    {
        Directory.Delete(updateRoot, recursive: true);
    }
}
var unlink = new MacSemaphore().Unlink();
if (!OperatingSystem.IsMacOS())
{
    Check(unlink.Status == SingletonReleaseStatus.NotMacOS,
        "The native semaphore was invoked or misreported off macOS.");
}

Console.WriteLine($"macOS platform safety tests passed: {passed}; skipped: {skipped}.");

sealed class RecordingCommandRunner(
    MacProcessCommandResult signatureResult,
    string packageIdentifier = "com.example.roblox.pkg",
    string packageVersion = "3",
    string architecture = "arm64",
    bool includeUnexpectedPayload = false,
    bool includeScripts = false,
    bool includeScriptDeclaration = false) : IMacProcessCommandRunner
{
    public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];
    public MacProcessCommandResult SignatureResult => signatureResult;

    public Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        Calls.Add((executable, values));
        if (string.Equals(executable, "/usr/sbin/pkgutil", StringComparison.Ordinal)
            && values.Contains("--check-signature", StringComparer.Ordinal))
        {
            return Task.FromResult(signatureResult);
        }

        if (string.Equals(executable, "/usr/sbin/pkgutil", StringComparison.Ordinal)
            && values.Contains("--expand-full", StringComparer.Ordinal))
        {
            var expansionRoot = values[^1];
            var appContents = Path.Combine(expansionRoot, "Payload", "Roblox Account Manager.app", "Contents");
            var payload = Path.Combine(appContents, "MacOS");
            Directory.CreateDirectory(payload);
            var scriptXml = includeScriptDeclaration ? "<scripts><custom file=\"run-me\" /></scripts>" : string.Empty;
            File.WriteAllText(Path.Combine(expansionRoot, "PackageInfo"),
                $"<pkg-info identifier=\"{packageIdentifier}\" version=\"{packageVersion}\" install-location=\"/Applications\">{scriptXml}</pkg-info>");
            File.WriteAllText(Path.Combine(appContents, "Info.plist"),
                "<?xml version=\"1.0\"?><plist><dict>" +
                "<key>CFBundleIdentifier</key><string>io.github.codysimonds65.roblox-account-manager</string>" +
                "<key>CFBundleExecutable</key><string>RobloxAccountManager</string>" +
                "<key>CFBundleShortVersionString</key><string>3.0</string>" +
                "</dict></plist>");
            File.WriteAllBytes(Path.Combine(payload, "RobloxAccountManager"), [1, 2, 3]);
            if (includeUnexpectedPayload)
            {
                var unexpected = Path.Combine(expansionRoot, "Payload", "Library", "LaunchDaemons");
                Directory.CreateDirectory(unexpected);
                File.WriteAllText(Path.Combine(unexpected, "unexpected.plist"), "not allowed");
            }
            if (includeScripts)
            {
                var scripts = Path.Combine(expansionRoot, "Scripts");
                Directory.CreateDirectory(scripts);
                File.WriteAllText(Path.Combine(scripts, "postinstall"), "#!/bin/sh");
            }
            return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
        }

        if (string.Equals(executable, "/usr/bin/lipo", StringComparison.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(0, $"Non-fat file: {architecture}", string.Empty));
        }

        return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
    }
}
