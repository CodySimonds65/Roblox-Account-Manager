using RobloxAccountManager.Platform.MacOS;
using Contracts = RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;
using System.Net;
using System.Text.Json;
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

var installerHandoff = Contracts.UpdateInstallResult.InstallerOpened();
Check(installerHandoff.Accepted && installerHandoff.DiagnosticCode == "installer-opened",
    "The macOS update result did not distinguish an installer handoff from completed installation.");

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
var redactedLaunchScheme = MacRobloxDiagnostics.RedactSensitive(
    "roblox-player:1+gameinfo:private-ticket-secret");
Check(!redactedLaunchScheme.Contains("private-ticket-secret", StringComparison.Ordinal),
    "A single-colon Roblox launch scheme was not redacted from diagnostics.");
var openDiagnostic = MacLaunchDiagnostics.DescribeOpenFailure(
    new MacProcessCommandResult(
        1,
        "stdout-ticket-secret",
        "LSOpenURLsWithRole failed for roblox-player://ticket=secret"));
Check(openDiagnostic.Contains("exit=1", StringComparison.Ordinal)
      && openDiagnostic.Contains("LSOpenURLsWithRole", StringComparison.Ordinal)
      && !openDiagnostic.Contains("stdout-ticket-secret", StringComparison.Ordinal)
      && !openDiagnostic.Contains("ticket=secret", StringComparison.Ordinal),
    "The macOS open failure diagnostic did not preserve safe stderr context or redact ticket data.");

var verificationDiagnostic = MacLaunchDiagnostics.DescribeVerificationFailure(
    new LaunchVerificationResult(
        LaunchVerificationStatus.TimedOut,
        null,
        false,
        ["No new stable Roblox process identity was observed (before=0; candidates=0; final=0)."]));
Check(verificationDiagnostic.Contains("macos-process-verification-timeout", StringComparison.Ordinal)
      && verificationDiagnostic.Contains("before=0", StringComparison.Ordinal),
    "The macOS process verification diagnostic did not retain the observed process counts.");

if (OperatingSystem.IsMacOS())
{
    var openFailure = await new MacProcessCommandRunner().RunAsync(
        "/usr/bin/open",
        ["-a", "/definitely/missing/Roblox.app", "roblox-player:1+gameinfo:ticket-secret"]);
    Check(!openFailure.Succeeded && !string.IsNullOrWhiteSpace(openFailure.StandardError),
        "The macOS open handoff discarded its stderr diagnostics.");
    Check(string.IsNullOrWhiteSpace(openFailure.StandardOutput),
        "The macOS open handoff retained stdout that could contain a launch ticket.");
}
else
{
    Skip("The /usr/bin/open diagnostic capture test requires macOS.");
}

var verificationBundlePath = Path.Combine(Path.GetTempPath(), "ram-mac-verification-" + Guid.NewGuid().ToString("N") + ".app");
Directory.CreateDirectory(verificationBundlePath);
try
{
    var verification = await new MacLaunchVerificationService(new EmptyRobloxProcessLocator())
        .WaitForNewProcessAsync(
            new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, Array.Empty<RobloxProcessInfo>()),
            verificationBundlePath,
            TimeSpan.FromMilliseconds(20));
    Check(verification.Status == LaunchVerificationStatus.TimedOut
          && verification.Warnings.Any(warning => warning.Contains("before=0", StringComparison.Ordinal)
              && warning.Contains("candidates=0", StringComparison.Ordinal)
              && warning.Contains("final=0", StringComparison.Ordinal)),
        "A native Roblox launch timeout did not expose before/candidate/final process counts.");
}
finally
{
    Directory.Delete(verificationBundlePath, recursive: true);
}

var clientInfo = new MacBundleInfo(
    "/Applications/Roblox.app",
    MacBundleDiscovery.RobloxBundleIdentifier,
    "/Applications/Roblox.app/Contents/MacOS/RobloxPlayer",
    "2.700.0",
    "2700000",
    true,
    "bundle-fingerprint",
    "executable-fingerprint",
    "plist-fingerprint");
Check(MacRobloxDiagnostics.DescribeClient(clientInfo).Contains("2.700.0", StringComparison.Ordinal)
      && MacRobloxDiagnostics.DescribeClient(clientInfo).Contains("2700000", StringComparison.Ordinal),
    "The macOS Roblox client version/build was not formatted for startup diagnostics.");

var diagnosticsRoot = Path.Combine(Path.GetTempPath(), "ram-mac-diagnostics-" + Guid.NewGuid().ToString("N"));
try
{
    var logsRoot = Path.Combine(diagnosticsRoot, "logs");
    var artifactRoot = Path.Combine(diagnosticsRoot, "artifacts");
    Directory.CreateDirectory(logsRoot);
    var processStart = new DateTimeOffset(2026, 8, 20, 21, 50, 10, TimeSpan.Zero);
    var logPath = Path.Combine(logsRoot, "2.700.0_20260820T215010Z_Player_A1B2_last.log");
    await File.WriteAllLinesAsync(logPath,
    [
        "2026-08-20T21:50:11Z [FLog::Output] RobloxChannel has been set to production",
        "2026-08-20T21:50:12Z [FLog::UpdateController] updateRequired TRUE",
        "2026-08-20T21:50:13Z [FLog::Network] Sending disconnect with reason: 285",
        "2026-08-20T21:50:14Z [FLog::Error] launch failed at https://www.roblox.com/share?code=share-secret&type=Server token=auth-ticket-token Cookie: session-secret authorization=Bearer-auth access_token=access-secret"
    ]);

    var diagnostics = MacRobloxDiagnostics.Collect(
        processStart,
        [logsRoot],
        artifactRoot);
    Check(diagnostics.StatusCode == "matched-session-log" && diagnostics.LogVersion == "2.700.0",
        "The matching macOS Roblox session log was not selected.");
    Check(diagnostics.Summary.Any(line => line.Contains("production", StringComparison.Ordinal))
          && diagnostics.Summary.Any(line => line.Contains("required", StringComparison.OrdinalIgnoreCase))
          && diagnostics.Summary.Any(line => line.Contains("285", StringComparison.Ordinal)),
        "The macOS Roblox session markers were not summarized.");
    Check(diagnostics.RedactedTail.Any(line => line.Contains("[REDACTED]", StringComparison.Ordinal))
          && diagnostics.RedactedTail.All(line => !line.Contains("share-secret", StringComparison.Ordinal))
          && diagnostics.RedactedTail.All(line => !line.Contains("auth-ticket-token", StringComparison.Ordinal))
          && diagnostics.RedactedTail.All(line => !line.Contains("session-secret", StringComparison.Ordinal))
          && diagnostics.RedactedTail.All(line => !line.Contains("Bearer-auth", StringComparison.Ordinal))
          && diagnostics.RedactedTail.All(line => !line.Contains("access-secret", StringComparison.Ordinal)),
        "Sensitive Roblox launch data was retained in the redacted log tail.");
    Check(diagnostics.ArtifactPath is not null && File.Exists(diagnostics.ArtifactPath)
          && !File.ReadAllText(diagnostics.ArtifactPath).Contains("share-secret", StringComparison.Ordinal),
        "The macOS Roblox diagnostic artifact was not written safely.");

    var missing = MacRobloxDiagnostics.Collect(
        processStart.AddHours(1),
        [logsRoot]);
    Check(missing.StatusCode == "session-log-not-found",
        "A missing macOS Roblox session log did not produce a safe diagnostic code.");

    var crashStart = DateTimeOffset.UtcNow.AddSeconds(-10);
    var crashRoot = Path.Combine(diagnosticsRoot, "crash-reports");
    Directory.CreateDirectory(crashRoot);
    var crashPath = Path.Combine(crashRoot, "RobloxPlayer_vm-crash.ips");
    await File.WriteAllTextAsync(crashPath,
        "{\"process\":\"RobloxPlayer\",\"exception\":\"EXC_CRASH\",\"url\":\"https://www.roblox.com/share?code=crash-secret\",\"authorization\":\"Bearer crash-token\"}");
    File.SetLastWriteTimeUtc(crashPath, crashStart.UtcDateTime.AddSeconds(2));
    var crash = MacRobloxDiagnostics.Collect(crashStart, [crashRoot], artifactRoot);
    Check(crash.StatusCode == "crash-report-found"
          && crash.Summary.Any(line => line.Contains("crash report", StringComparison.OrdinalIgnoreCase)),
        "A Roblox crash report was not surfaced when no session log matched.");
    Check(crash.RedactedTail.Any(line => line.Contains("[REDACTED]", StringComparison.Ordinal))
          && crash.RedactedTail.All(line => !line.Contains("crash-secret", StringComparison.Ordinal))
          && crash.RedactedTail.All(line => !line.Contains("crash-token", StringComparison.Ordinal)),
        "Sensitive data was retained in the redacted Roblox crash report.");
}
finally
{
    if (Directory.Exists(diagnosticsRoot)) Directory.Delete(diagnosticsRoot, recursive: true);
}

var unconfiguredDiscovery = new MacBundleDiscovery();
_ = new MacCorePlatformLauncher(unconfiguredDiscovery);
passed++;
Console.WriteLine("PASS: macOS launcher composes without a maintainer-only Roblox Team ID pin.");

{
    var runtimeTestRoot = Path.Combine(Path.GetTempPath(), "ram-managed-runtime-" + Guid.NewGuid().ToString("N"));
    var approvedRoot = Path.Combine(runtimeTestRoot, "Applications");
    var sourceBundle = Path.Combine(approvedRoot, "Roblox.app");
    var sourceContents = Path.Combine(sourceBundle, "Contents");
    var sourceMacOs = Path.Combine(sourceContents, "MacOS");
    var runtimeRoot = Path.Combine(runtimeTestRoot, "runtime");
    Directory.CreateDirectory(sourceMacOs);
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(sourceContents, "Info.plist"),
            "<?xml version=\"1.0\"?><plist><dict>" +
            "<key>CFBundleIdentifier</key><string>com.roblox.RobloxPlayer</string>" +
            "<key>CFBundleExecutable</key><string>RobloxPlayer</string>" +
            "<key>CFBundleShortVersionString</key><string>1.0</string>" +
            "<key>CFBundleVersion</key><string>1</string>" +
            "<key>LSMultipleInstancesProhibited</key><true/>" +
            "</dict></plist>");
        await File.WriteAllBytesAsync(Path.Combine(sourceMacOs, "RobloxPlayer"), [1, 2, 3, 4]);

        var runtimeRunner = new ManagedRuntimeTestCommandRunner();
        var testDiscovery = new MacBundleDiscovery(
            runtimeRunner,
            new MacSignatureVerifier(runtimeRunner),
            [approvedRoot]);
        var builder = new MacManagedRuntimeBuilder(
            runtimeRoot,
            testDiscovery,
            runtimeRunner,
            processLocator: new EmptyRobloxProcessLocator());
        var buildRequest = new MacManagedRuntimeRequest(
            sourceBundle,
            "single-runtime",
            UserConsented: true,
            Level: MacLaunchLevel.ManagedRuntime);
        var built = await builder.BuildAsync(buildRequest);
        Check(built.Status == MacRuntimeBuildStatus.Built && built.RuntimePath is not null,
            "The managed Roblox runtime was not built from a validated source.");
        var managedPlist = Path.Combine(built.RuntimePath!, "Roblox.app", "Contents", "Info.plist");
        var managedPlistText = await File.ReadAllTextAsync(managedPlist);
        var sourcePlistText = await File.ReadAllTextAsync(Path.Combine(sourceContents, "Info.plist"));
        Check(managedPlistText.Contains("LSMultipleInstancesProhibited", StringComparison.Ordinal)
              && managedPlistText.Contains("<false", StringComparison.Ordinal)
              && sourcePlistText.Contains("<true", StringComparison.Ordinal),
            "Managed runtime preparation did not disable the Launch Services guard while preserving the source bundle.");
        var reused = await builder.BuildAsync(buildRequest);
        Check(reused.Status == MacRuntimeBuildStatus.Reused,
            "An unchanged managed Roblox runtime was rebuilt instead of reused.");
        await File.WriteAllTextAsync(
            managedPlist,
            managedPlistText.Replace("<false", "<true", StringComparison.Ordinal)
                .Replace("</false>", "</true>", StringComparison.Ordinal));
        var repaired = await builder.BuildAsync(buildRequest);
        Check(repaired.Status == MacRuntimeBuildStatus.Built,
            "A managed runtime with the Launch Services guard restored was incorrectly reused.");

        var slotManager = new MacManagedRuntimeSlotManager(
            runtimeRoot,
            testDiscovery,
            runtimeRunner,
            processLocator: new EmptyRobloxProcessLocator());
        var coreStrategy = new MacCoreMultiInstanceStrategy(
            slotManager: slotManager,
            bundleDiscovery: testDiscovery);
        var deniedPreparation = await coreStrategy.PrepareAsync(new Contracts.RobloxLaunchRequest(
            "test-account",
            _ => ValueTask.FromResult(new Uri("roblox-player:1+gameinfo:test")),
            PreferredMacLevel: Contracts.MacLaunchLevel.ManagedSlots,
            RobloxBundlePath: sourceBundle,
            UserConsentedToMultiInstanceChanges: false));
        Check(!deniedPreparation.Succeeded && deniedPreparation.DiagnosticCode == "consent-required",
            "macOS managed-runtime preparation did not enforce explicit consent.");
        if (OperatingSystem.IsMacOS())
        {
            var prepared = await coreStrategy.PrepareAsync(deniedPreparation.Request with
            {
                UserConsentedToMultiInstanceChanges = true
            });
            Check(prepared.Succeeded
                  && prepared.ActiveMacLevel == Contracts.MacLaunchLevel.ManagedSlots
                  && prepared.Request.RobloxBundlePath?.Contains("slot-1", StringComparison.Ordinal) == true
                  && !string.IsNullOrWhiteSpace(prepared.Request.ValidatedRobloxBundleFingerprint),
                "The production macOS strategy did not prepare a validated managed slot.");
            await prepared.Lease!.DisposeAsync();
        }
        var firstSlot = await slotManager.AcquireAsync(buildRequest);
        var secondSlot = await slotManager.AcquireAsync(buildRequest);
        Check(firstSlot.Succeeded && secondSlot.Succeeded
              && firstSlot.Slot?.SlotNumber == 1
              && secondSlot.Slot?.SlotNumber == 2,
            "A reserved managed slot was reused before its launch attempt completed.");
        await firstSlot.Lease!.DisposeAsync();
        await secondSlot.Lease!.DisposeAsync();
        var reusedSlot = await slotManager.AcquireAsync(buildRequest);
        Check(reusedSlot.Succeeded && reusedSlot.Slot?.SlotNumber == 1,
            "An idle managed slot was not reusable after its reservation was released.");
        await reusedSlot.Lease!.DisposeAsync();
        if (OperatingSystem.IsMacOS())
        {
            Check(runtimeRunner.Calls.Any(call =>
                    call.Executable == "/usr/bin/codesign"
                    && call.Arguments.Contains("--options", StringComparer.Ordinal)
                    && call.Arguments.Contains("runtime", StringComparer.Ordinal)),
                "The managed Roblox clone was not signed with hardened-runtime options.");
        }
    }
    finally
    {
        if (Directory.Exists(runtimeTestRoot)) Directory.Delete(runtimeTestRoot, recursive: true);
    }
}
var plistMetadataFallbackRoot = Path.Combine(
    Path.GetTempPath(),
    "ram-mac-roblox-plist-fallback-" + Guid.NewGuid().ToString("N") + ".app");
var plistMetadataFallbackContents = Path.Combine(plistMetadataFallbackRoot, "Contents");
var plistMetadataFallbackMacOs = Path.Combine(plistMetadataFallbackContents, "MacOS");
Directory.CreateDirectory(plistMetadataFallbackMacOs);
try
{
    await File.WriteAllTextAsync(
        Path.Combine(plistMetadataFallbackContents, "Info.plist"),
        "<?xml version=\"1.0\"?><plist><dict>" +
        "<key>CFBundleExecutable</key><string>RobloxPlayer</string>" +
        "</dict></plist>");
    await File.WriteAllBytesAsync(
        Path.Combine(plistMetadataFallbackMacOs, "RobloxPlayer"),
        [1, 2, 3]);

    var plistMetadataFallback = await new MacBundleDiscovery(
            new RobloxMetadataFallbackCommandRunner())
        .ValidateManagedRuntimeAsync(plistMetadataFallbackRoot);
    Check(plistMetadataFallback is not null
          && plistMetadataFallback.BundleIdentifier == MacBundleDiscovery.RobloxBundleIdentifier
          && plistMetadataFallback.ExecutablePath.EndsWith(
              Path.Combine("Contents", "MacOS", "RobloxPlayer"),
              StringComparison.OrdinalIgnoreCase),
        "A Roblox bundle without root CFBundleIdentifier metadata was rejected.");
}
finally
{
    if (Directory.Exists(plistMetadataFallbackRoot))
        Directory.Delete(plistMetadataFallbackRoot, recursive: true);
}

var officialSignature = new MacProcessCommandResult(
    0,
    string.Empty,
    "Authority=Developer ID Application: Roblox Corporation (ARBITRARY1)\n" +
    "Identifier=com.roblox.RobloxPlayer\n" +
    "TeamIdentifier=ARBITRARY1");
var officialGatekeeper = new MacProcessCommandResult(0, "accepted", string.Empty);
var officialRequirements = new MacProcessCommandResult(
    0,
    string.Empty,
    "designated => anchor apple generic and identifier \"com.roblox.RobloxPlayer\"");
Check(MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature, officialGatekeeper, officialRequirements),
    "A valid Developer ID Roblox bundle was rejected when Team ID pinning was omitted.");
if (OperatingSystem.IsMacOS())
{
    var signatureCommandRunner = new RecordingSignatureCommandRunner();
    var verified = await new MacSignatureVerifier(signatureCommandRunner)
        .VerifyAsync("/Applications/Roblox.app");
    var requirementCall = signatureCommandRunner.Calls.Single(call =>
        call.Arguments.Contains("--requirements", StringComparer.Ordinal));
    Check(verified
          && requirementCall.Arguments.Contains("-", StringComparer.Ordinal)
          && !requirementCall.Arguments.Contains(":-", StringComparer.Ordinal),
        "macOS signature verification did not request the designated requirement in the supported format.");
}
else
{
    Skip("The macOS signature command-shape test requires macOS.");
}
Check(MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature with { StandardError = officialSignature.StandardError.Replace(
            "Roblox Corporation", "Other Developer") },
        officialGatekeeper,
        officialRequirements),
    "The intentionally unpinned Team ID policy unexpectedly rejected another Developer ID signer.");
Console.WriteLine("PASS: publisher Team ID differences remain an explicit accepted trust reduction.");
Check(!MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature with { StandardError = officialSignature.StandardError.Replace(
            "Identifier=com.roblox.RobloxPlayer", "Identifier=com.example.Spoof") },
        officialGatekeeper,
        officialRequirements),
    "A bundle with a spoofed Roblox identifier was accepted.");
Check(!MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature with { StandardError = officialSignature.StandardError + "\nSignature=adhoc" },
        officialGatekeeper,
        officialRequirements),
    "An ad-hoc Roblox bundle was accepted.");
Check(!MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature,
        new MacProcessCommandResult(1, string.Empty, "rejected"),
        officialRequirements),
    "A bundle rejected by Gatekeeper was accepted.");
Check(!MacSignatureVerifier.IsAcceptedOfficialBundleSignature(
        officialSignature,
        officialGatekeeper,
        officialRequirements with { StandardError = "identifier \"com.example.Spoof\"" }),
    "A bundle with a spoofed designated requirement was accepted.");

var raceBundlePath = Path.Combine(Path.GetTempPath(), "ram-mac-race-" + Guid.NewGuid().ToString("N") + ".app");
Directory.CreateDirectory(raceBundlePath);
try
{
    var raceBundle = new MacBundleInfo(
        raceBundlePath,
        MacBundleDiscovery.RobloxBundleIdentifier,
        Path.Combine(raceBundlePath, "Contents", "MacOS", "RobloxPlayer"),
        "1",
        "1",
        true,
        "fingerprint-a",
        "executable-a",
        "plist-a");
    var replacedBundle = raceBundle with { SourceFingerprint = "fingerprint-b" };
    var validationCalls = 0;
    var raceLocator = new SequenceRobloxProcessLocator(raceBundlePath);
var raceVerification = await new MacLaunchVerificationService(
            raceLocator,
            (_, _) => Task.FromResult<MacBundleInfo?>(Interlocked.Increment(ref validationCalls) <= 2 ? raceBundle : replacedBundle))
        .WaitForNewProcessAsync(
            new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, Array.Empty<RobloxProcessInfo>()),
            raceBundlePath,
            TimeSpan.FromSeconds(2));
    Check(raceVerification.Status == LaunchVerificationStatus.InvalidBundle
        && raceVerification.Warnings.Any(warning => warning.Contains("bundle changed", StringComparison.OrdinalIgnoreCase)),
        "A Roblox bundle replacement during process verification was not rejected.");

var boundaryLocator = new SequenceRobloxProcessLocator(raceBundlePath);
var boundaryVerification = await new MacLaunchVerificationService(
        boundaryLocator,
        (_, _) => Task.FromResult<MacBundleInfo?>(replacedBundle))
    .WaitForNewProcessAsync(
        new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, Array.Empty<RobloxProcessInfo>()),
        raceBundlePath,
        TimeSpan.FromSeconds(2),
        expectedBundleFingerprint: raceBundle.SourceFingerprint);
Check(boundaryVerification.Status == LaunchVerificationStatus.InvalidBundle
    && boundaryVerification.Warnings.Any(warning => warning.Contains("bundle changed", StringComparison.OrdinalIgnoreCase)),
    "A bundle replacement at the post-open verification boundary was accepted as the baseline.");
}
finally
{
    Directory.Delete(raceBundlePath, recursive: true);
}

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
        "{\"schemaVersion\":2,\"id\":\"sample.plugin\",\"capabilities\":[\"host.accounts.read\"],\"entryPoints\":{\"osx-arm64\":\"plugin\",\"osx-x64\":\"plugin\"}}");
    await File.WriteAllBytesAsync(Path.Combine(pluginDirectory, "plugin"), [1, 2, 3]);
    var pluginHost = new MacPluginHostFacade(pluginRoot);
    var pluginIds = await pluginHost.GetInstalledPluginIdsAsync();
    Check(pluginIds.Contains("sample.plugin", StringComparer.Ordinal),
        "A macOS RID-matched plugin was not discovered.");
    var transport = new MacUnixPluginTransport();
    Check(System.Text.Encoding.UTF8.GetByteCount(transport.SocketPath) <= 104,
        "The macOS plugin socket path exceeded the sockaddr_un limit.");
    await transport.DisposeAsync();
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
    var explicitRootInstaller = new MacPkgUpdateInstaller(
        new RecordingCommandRunner(signedRunner.SignatureResult, useApplicationsPayload: true),
        expectedRid: "osx-arm64",
        trust,
        updateRoot,
        "2",
        "2.0");
    Check(await explicitRootInstaller.ValidateAsync(validPackage) is null,
        "A PKG with an explicit Applications/ payload root was rejected.");
    var lipoCall = signedRunner.Calls.LastOrDefault(call => string.Equals(call.Executable, "/usr/bin/lipo", StringComparison.Ordinal));
    Check(lipoCall.Executable is not null
        && lipoCall.Arguments.Count == 2
        && lipoCall.Arguments[0] == "-info"
        && lipoCall.Arguments[1].EndsWith(Path.Combine("Contents", "MacOS", "RobloxAccountManager"), StringComparison.Ordinal),
        "The architecture check passed an unsupported option terminator to macOS lipo.");
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
    Check(wrongVersion.CurrentPackageVersion == 2,
        "The installer did not expose the installed numeric PKG version for diagnostics.");
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

var sourceRoot = Path.Combine(Path.GetTempPath(), "ram-mac-update-source-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sourceRoot);
try
{
    var packageBytes = RandomNumberGenerator.GetBytes(512);
    var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
    var validPackageName = "RobloxAccountManager-3.1.0-osx-arm64.pkg";
    var validChecksumName = validPackageName + ".sha256";
    var releaseJson = BuildReleaseJson(
        ("draft", true, "v9.0.0", validPackageName, validChecksumName),
        ("prerelease", true, "v8.0.0", validPackageName, validChecksumName),
        ("stable", false, "v7.0.0", "RobloxAccountManager-7.0.0-osx-x64.pkg", "RobloxAccountManager-7.0.0-osx-x64.pkg.sha256"),
        ("stable", false, "v3.1.0", validPackageName, validChecksumName));
    var handler = new UpdateHttpHandler(releaseJson, packageBytes, packageHash, validPackageName);
    var source = new MacGitHubReleaseUpdateSource(
        new HttpClient(handler),
        new RecordingCommandRunner(new MacProcessCommandResult(0, string.Empty, string.Empty),
            packageIdentifier: "io.github.codysimonds65.roblox-account-manager", packageVersion: "77"),
        rid: "osx-arm64",
        stagingRoot: sourceRoot);
    var signedPackage = await source.DownloadLatestAsync(UpdateChannel.Signed);
    Check(signedPackage is not null && !signedPackage.IsUnsigned
        && signedPackage.Version == new Version(3, 1, 0)
        && signedPackage.PackageVersion == "77"
        && signedPackage.LocalPath.StartsWith(sourceRoot, StringComparison.Ordinal),
        "The GitHub source did not skip drafts/prereleases/wrong-RID assets or read PackageInfo version.");

    var unsignedName = "RobloxAccountManager-3.1.0-osx-arm64-unsigned.pkg";
    var unsignedHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
    var unsignedSource = new MacGitHubReleaseUpdateSource(
        new HttpClient(new UpdateHttpHandler(BuildReleaseJson(("stable", false, "v3.1.0", unsignedName, unsignedName + ".sha256")), packageBytes, unsignedHash, unsignedName)),
        new RecordingCommandRunner(new MacProcessCommandResult(0, string.Empty, string.Empty),
            packageIdentifier: "io.github.codysimonds65.roblox-account-manager", packageVersion: "77"),
        rid: "osx-arm64",
        stagingRoot: Path.Combine(sourceRoot, "unsigned"));
    var unsignedPackage = await unsignedSource.DownloadLatestAsync(UpdateChannel.Unsigned);
    Check(unsignedPackage is not null && unsignedPackage.IsUnsigned
        && unsignedPackage.LocalPath.EndsWith("-unsigned.pkg", StringComparison.Ordinal),
        "The unsigned channel did not select the explicit unsigned asset.");

    var mismatchSource = new MacGitHubReleaseUpdateSource(
        new HttpClient(new UpdateHttpHandler(BuildReleaseJson(("stable", false, "v3.2.0", "RobloxAccountManager-3.2.0-osx-arm64.pkg", "RobloxAccountManager-3.2.0-osx-arm64.pkg.sha256")), packageBytes, new string('0', 64), "RobloxAccountManager-3.2.0-osx-arm64.pkg")),
        new RecordingCommandRunner(new MacProcessCommandResult(0, string.Empty, string.Empty),
            packageIdentifier: "io.github.codysimonds65.roblox-account-manager", packageVersion: "77"),
        rid: "osx-arm64",
        stagingRoot: Path.Combine(sourceRoot, "mismatch"));
    try
    {
        _ = await mismatchSource.DownloadLatestAsync(UpdateChannel.Signed);
        throw new InvalidOperationException("A SHA-256 mismatch was accepted by the GitHub source.");
    }
    catch (InvalidDataException) { passed++; }
}
finally
{
    if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
}
var unlink = new MacSemaphore().Unlink();
if (!OperatingSystem.IsMacOS())
{
    Check(unlink.Status == SingletonReleaseStatus.NotMacOS,
        "The native semaphore was invoked or misreported off macOS.");
}

Console.WriteLine($"macOS platform safety tests passed: {passed}; skipped: {skipped}.");

static string BuildReleaseJson(params (string Kind, bool Prerelease, string Tag, string Package, string Checksum)[] releases) =>
    JsonSerializer.Serialize(releases.Select(release => new
    {
        draft = release.Kind == "draft",
        prerelease = release.Prerelease,
        tag_name = release.Tag,
        assets = new[]
        {
            new { name = release.Package, browser_download_url = $"https://downloads.example.test/{release.Package}" },
            new { name = release.Checksum, browser_download_url = $"https://downloads.example.test/{release.Checksum}" }
        }
    }));

sealed class UpdateHttpHandler(string releaseJson, byte[] packageBytes, string checksum, string packageName) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri?.AbsolutePath.Contains("/releases", StringComparison.Ordinal) == true)
            return Task.FromResult(Response(releaseJson, "application/json"));
        if (request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) == true)
            return Task.FromResult(Response($"{checksum}  {packageName}\n", "text/plain"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(packageBytes)
        });
    }

    private static HttpResponseMessage Response(string content, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
    };
}

sealed class RecordingCommandRunner(
    MacProcessCommandResult signatureResult,
    string packageIdentifier = "com.example.roblox.pkg",
    string packageVersion = "3",
    string architecture = "arm64",
    bool includeUnexpectedPayload = false,
    bool includeScripts = false,
    bool includeScriptDeclaration = false,
    bool useApplicationsPayload = false) : IMacProcessCommandRunner
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
            && values.Contains("--", StringComparer.Ordinal))
        {
            // pkgutil's subcommands take positional paths directly; an option terminator is
            // not part of its command grammar. Keep this fake strict so the macOS regression
            // cannot silently return to the failing invocation used by the VM package.
            return Task.FromResult(new MacProcessCommandResult(64, string.Empty, "invalid option --"));
        }

        if (string.Equals(executable, "/usr/sbin/pkgutil", StringComparison.Ordinal)
            && values.Contains("--check-signature", StringComparer.Ordinal))
        {
            return Task.FromResult(signatureResult);
        }

        if (string.Equals(executable, "/usr/sbin/pkgutil", StringComparison.Ordinal)
            && values.Contains("--expand-full", StringComparer.Ordinal))
        {
            var expansionRoot = values[^1];
            var payloadAppRoot = Path.Combine(expansionRoot, "Payload",
                useApplicationsPayload ? "Applications/Roblox Account Manager.app" : "Roblox Account Manager.app");
            var appContents = Path.Combine(payloadAppRoot, "Contents");
            var payload = Path.Combine(appContents, "MacOS");
            Directory.CreateDirectory(payload);
            var scriptXml = includeScriptDeclaration ? "<scripts><custom file=\"run-me\" /></scripts>" : string.Empty;
            var installLocation = useApplicationsPayload ? "/" : "/Applications";
            File.WriteAllText(Path.Combine(expansionRoot, "PackageInfo"),
                $"<pkg-info identifier=\"{packageIdentifier}\" version=\"{packageVersion}\" install-location=\"{installLocation}\">{scriptXml}</pkg-info>");
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
            if (values.Contains("--", StringComparer.Ordinal))
            {
                // macOS lipo does not accept an option terminator before its positional input.
                // Keep this fake strict so the real VM invocation cannot regress unnoticed.
                return Task.FromResult(new MacProcessCommandResult(64, string.Empty, "unknown flag: --"));
            }

            return Task.FromResult(new MacProcessCommandResult(0, $"Non-fat file: {architecture}", string.Empty));
        }

        return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
    }
}

sealed class RobloxMetadataFallbackCommandRunner : IMacProcessCommandRunner
{
    public Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        if (string.Equals(executable, "/usr/bin/plutil", StringComparison.Ordinal))
        {
            var key = values.Length > 1 ? values[1] : string.Empty;
            return string.Equals(key, "CFBundleExecutable", StringComparison.Ordinal)
                ? Task.FromResult(new MacProcessCommandResult(0, "RobloxPlayer", string.Empty))
                : Task.FromResult(new MacProcessCommandResult(1, string.Empty, "missing plist key"));
        }

        if (string.Equals(executable, "/usr/bin/codesign", StringComparison.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
        }

        return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
    }
}

sealed class RecordingSignatureCommandRunner : IMacProcessCommandRunner
{
    public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        Calls.Add((executable, values));
        if (values.Contains("--requirements", StringComparer.Ordinal))
        {
            var formatIndex = Array.IndexOf(values, "--requirements") + 1;
            var requirements = formatIndex < values.Length && values[formatIndex] == "-"
                ? "designated => identifier \"com.roblox.RobloxPlayer\""
                : string.Empty;
            return Task.FromResult(new MacProcessCommandResult(0, requirements, string.Empty));
        }

        if (string.Equals(executable, "/usr/bin/codesign", StringComparison.Ordinal)
            && values.Contains("--verbose=4", StringComparer.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(
                0,
                string.Empty,
                "Authority=Developer ID Application: Roblox Corporation (ARBITRARY1)\n" +
                "Identifier=com.roblox.RobloxPlayer"));
        }

        return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
    }
}

sealed class SequenceRobloxProcessLocator : IRobloxProcessLocator
{
    private readonly RobloxProcessInfo _candidate;
    private int _captureCount;

    public SequenceRobloxProcessLocator(string bundlePath)
    {
        var executablePath = Path.Combine(bundlePath, "Contents", "MacOS", "RobloxPlayer");
        _candidate = new RobloxProcessInfo(
            new RobloxProcessIdentity(
                4242,
                DateTimeOffset.UtcNow,
                executablePath,
                bundlePath),
            "RobloxPlayer",
            false,
            true);
    }

    public RobloxLaunchSnapshot CaptureSnapshot()
    {
        var processes = Interlocked.Increment(ref _captureCount) == 1
            ? Array.Empty<RobloxProcessInfo>()
            : [_candidate];
        return new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, processes);
    }

    public RobloxProcessInfo? FindProcess(int processId) =>
        processId == _candidate.ProcessId ? _candidate : null;

    public bool IsSameProcess(RobloxProcessIdentity expected, RobloxProcessInfo actual) =>
        expected.Matches(actual.Identity) && actual.IsStable;
}

sealed class EmptyRobloxProcessLocator : IRobloxProcessLocator
{
    public RobloxLaunchSnapshot CaptureSnapshot() =>
        new(DateTimeOffset.UtcNow, Array.Empty<RobloxProcessInfo>());

    public RobloxProcessInfo? FindProcess(int processId) => null;

    public bool IsSameProcess(RobloxProcessIdentity expected, RobloxProcessInfo actual) => false;
}

sealed class ManagedRuntimeTestCommandRunner : IMacProcessCommandRunner
{
    private readonly MacProcessCommandRunner _native = new();
    public List<(string Executable, string[] Arguments)> Calls { get; } = [];

    public Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var values = arguments.ToArray();
        Calls.Add((executable, values));
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));

        if (string.Equals(executable, "/bin/cp", StringComparison.Ordinal)
            || string.Equals(executable, "/usr/bin/plutil", StringComparison.Ordinal))
        {
            return _native.RunAsync(executable, values, cancellationToken);
        }

        if (string.Equals(executable, "/usr/sbin/spctl", StringComparison.Ordinal))
            return Task.FromResult(new MacProcessCommandResult(0, "accepted", string.Empty));

        if (!string.Equals(executable, "/usr/bin/codesign", StringComparison.Ordinal))
            return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));

        if (values.Contains("--requirements", StringComparer.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(
                0,
                string.Empty,
                "designated => anchor apple generic and identifier \"com.roblox.RobloxPlayer\""));
        }

        if (values.Contains("--verbose=4", StringComparer.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(
                0,
                string.Empty,
                "Authority=Developer ID Application: Roblox Corporation (TESTTEAM)\n" +
                "Identifier=com.roblox.RobloxPlayer\nTeamIdentifier=TESTTEAM"));
        }

        if (values.Contains("--entitlements", StringComparer.Ordinal)
            && values.Contains(":-", StringComparer.Ordinal))
        {
            return Task.FromResult(new MacProcessCommandResult(
                0,
                string.Empty,
                "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>"));
        }

        return Task.FromResult(new MacProcessCommandResult(0, string.Empty, string.Empty));
    }
}
