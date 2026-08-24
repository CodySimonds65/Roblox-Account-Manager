using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Core.Navigation;
using RobloxAccountManager.Desktop;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Platform.MacOS;
using System.Text.Json;
using MacProcessIdentity = RobloxAccountManager.Core.Contracts.RobloxProcessIdentity;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var refreshKinds = new List<bool>();
var refreshScheduler = new ClientOverlayRefreshScheduler(async explicitSelection =>
{
    refreshKinds.Add(explicitSelection);
    if (refreshKinds.Count == 1)
    {
        firstRefreshStarted.TrySetResult();
        await releaseFirstRefresh.Task;
    }
});
var passiveRefresh = refreshScheduler.RequestAsync();
await firstRefreshStarted.Task;
await refreshScheduler.RequestAsync(explicitUserSelection: true);
releaseFirstRefresh.TrySetResult();
await passiveRefresh;
Require(refreshKinds.SequenceEqual([false, true]),
    "An explicit client-tab selection was dropped while a passive overlay refresh was active.");

var readbackFailureText = ClientOverlayFailureText.Describe(
    "accessibility-minimized-readback-mismatch:restore-overlay-failed");
Require(!readbackFailureText.Contains("Grant Accessibility permission", StringComparison.Ordinal)
        && readbackFailureText.Contains("retry restoration", StringComparison.OrdinalIgnoreCase),
    "A minimized-state restore failure was incorrectly described as a permission failure.");
Require(ClientOverlayFailureText.Describe("restore-overlay-failed")
            .Contains("retry restoration", StringComparison.OrdinalIgnoreCase),
    "A blocked navigation restore did not expose a retry action in its recovery text.");
Require(ClientOverlayFailureText.Describe("accessibility-permission-required")
            .Contains("Grant Accessibility permission", StringComparison.Ordinal),
    "An Accessibility permission failure lost its permission guidance.");
Require(!ClientOverlayFailureText.IsRetryable(MacOverlayOperationResult.Failure(
                "restore-overlay-failed",
                clients: [new MacOverlayClientDiagnostic("account", 1, "accessibility-frame-invalid", false, 0, 0, "restore", false)]))
        && ClientOverlayFailureText.IsRetryable(MacOverlayOperationResult.Failure(
                "restore-overlay-failed",
                clients: [new MacOverlayClientDiagnostic("account", 1, "accessibility-window-not-settled", false, 0, 0, "restore", true)])),
    "Hard restore failures were retried automatically or transient restore failures were not retryable.");
Require(DesktopPanelLayoutPolicy.CanRenderWithoutClipping(
            DesktopPanelLayoutPolicy.ContentMinimumHeight,
            DesktopPanelLayoutPolicy.ActivityMinimumHeight)
        && !DesktopPanelLayoutPolicy.CanRenderWithoutClipping(
            DesktopPanelLayoutPolicy.ContentMinimumHeight - 1,
            DesktopPanelLayoutPolicy.ActivityMinimumHeight)
        && !DesktopPanelLayoutPolicy.CanRenderWithoutClipping(
            DesktopPanelLayoutPolicy.ContentMinimumHeight,
            DesktopPanelLayoutPolicy.ActivityMinimumHeight - 1)
        && DesktopPanelLayoutPolicy.WindowMinimumHeight >= DesktopPanelLayoutPolicy.RequiredWindowHeight,
    "Desktop panel minimums did not protect the Clients, Browse, and Activity content from splitter clipping.");
Require(Math.Abs(DesktopPanelLayoutPolicy.GetMaximumActivityHeight(860, 150) - 250) < 0.001
        && DesktopPanelLayoutPolicy.GetMaximumActivityHeight(700, 150) == DesktopPanelLayoutPolicy.ActivityMinimumHeight
        && DesktopPanelLayoutPolicy.UseCompactPresetBar(1300)
        && !DesktopPanelLayoutPolicy.UseCompactPresetBar(1000),
    "The adaptive layout policy did not preserve browser space while leaving Activity resizable.");

var duplicateProcessIdentity = new MacProcessIdentity(
    101,
    DateTimeOffset.UtcNow.AddMinutes(-2),
    "/Applications/Roblox.app/Contents/MacOS/RobloxPlayer",
    "/Applications/Roblox.app",
    RobloxPlatform.MacOS);
var duplicateProcessIdentityTwo = duplicateProcessIdentity with
{
    Pid = 102,
    StartTimeUtc = duplicateProcessIdentity.StartTimeUtc.AddSeconds(1)
};
var uniqueProcessIdentity = duplicateProcessIdentity with
{
    Pid = 103,
    StartTimeUtc = duplicateProcessIdentity.StartTimeUtc.AddSeconds(2)
};
var discovery = MacClientWindowReconciliation.Reconcile([
    new RobloxWindowInfo(duplicateProcessIdentity, null, null, AccountId: "account-a"),
    new RobloxWindowInfo(duplicateProcessIdentityTwo, null, null, AccountId: "account-a"),
    new RobloxWindowInfo(uniqueProcessIdentity, null, null, AccountId: "account-b"),
    new RobloxWindowInfo(duplicateProcessIdentity with { Pid = 104 }, null, null)
]);
Require(discovery.StableWindows.Count == 1
        && discovery.StableWindows[0].AccountId == "account-b"
        && discovery.Duplicates.Count == 1
        && discovery.Duplicates[0].AccountId == "account-a"
        && discovery.Duplicates[0].ProcessIds.SequenceEqual([101, 102])
        && discovery.UnboundProcessCount == 1,
    "Duplicate or unbound managed macOS client records were not isolated before overlay operations.");

try
{
    _ = DesktopComposition.Create(RobloxPlatform.Windows);
    throw new InvalidOperationException("The macOS Avalonia composition accepted Windows.");
}
catch (PlatformNotSupportedException exception)
{
    Require(exception.Message.Contains("WPF", StringComparison.Ordinal),
        "The unsupported-platform diagnostic did not direct Windows users to the WPF frontend.");
}

var account = new AccountProfile { Id = Guid.NewGuid().ToString("N"), Label = "Test account" };
var accounts = new[] { account };

var normalStartup = DesktopStartupPlan.Create(accounts, DesktopValidationMode.None);
Require(ReferenceEquals(normalStartup.InitialAccount, account),
    "Normal startup did not preserve the first account selection.");
Require(!normalStartup.ActivateBrowserOnStartup,
    "Normal startup still activates a browser session before the user requests it.");

var guiStartup = DesktopStartupPlan.Create(accounts, DesktopValidationMode.GuiStartup);
Require(ReferenceEquals(guiStartup.InitialAccount, account) && !guiStartup.ActivateBrowserOnStartup,
    "GUI startup validation should exercise the shell without creating a WebView.");

var browserStartup = DesktopStartupPlan.Create(accounts, DesktopValidationMode.BrowserStartup);
Require(ReferenceEquals(browserStartup.InitialAccount, account) && browserStartup.ActivateBrowserOnStartup,
    "Browser startup validation did not opt into explicit WebView activation.");

var secondAccount = new AccountProfile { Id = Guid.NewGuid().ToString("N"), Label = "Second account" };
var thirdAccount = new AccountProfile { Id = Guid.NewGuid().ToString("N"), Label = "Third account" };
var remembered = DesktopStartupPlan.RestoreSelectedAccounts(
    [account, secondAccount, thirdAccount],
    new LauncherSettings
    {
        RememberSelections = true,
        LastSelectedProfileIds = [thirdAccount.Id, secondAccount.Id]
    });
Require(remembered.SequenceEqual([secondAccount, thirdAccount]),
    "Remembered account selection was not restored in account-list order.");
var fallback = DesktopStartupPlan.RestoreSelectedAccounts(
    [account, secondAccount],
    new LauncherSettings { RememberSelections = true, LastSelectedProfileIds = ["missing"] });
Require(fallback.SequenceEqual([account]),
    "A selection containing only removed accounts did not fall back to the first account.");

var presets = new List<GamePreset>
{
    new("Built-in", "https://www.roblox.com/games/123/Built-in", true)
};
presets.Add(new GamePreset("Added preset", "https://www.roblox.com/games/456/Added"));
Require(DesktopPresetPolicy.FilterPresets(presets, "added").Single().Name == "Added preset",
    "Preset filtering did not include a newly added preset.");

var customPreset = new GamePreset("Custom URL", "https://www.roblox.com/games/", true);
Require(DesktopPresetPolicy.IsCustomUrlPreset(customPreset) && DesktopPresetPolicy.GetUrlEditorValue(customPreset) == string.Empty,
    "The Custom URL preset did not expose an editable empty URL field.");
Require(DesktopPresetPolicy.TryResolveLaunchUrl(
        customPreset,
        "https://www.roblox.com/games/789/Typed-url",
        out var customUrl) && customUrl.Contains("/games/789/Typed-url", StringComparison.Ordinal),
    "The typed Custom URL was not used as the launch URL.");

Require(MacUpdateActivityFormatter.FormatUnsignedValidationRejection("pkg-version-not-newer", 77) ==
        "Unsigned update rejected before prompt: pkg-version-not-newer (installed pkg version: 77).",
    "The unsigned update rejection did not include the installed PKG version.");

Require(RobloxPlayControl.ParseResult("clicked") == RobloxPlayControlStatus.Clicked,
    "A clicked Roblox Play-control result was not recognized.");
Require(RobloxPlayControl.ParseResult("\"not-found\"") == RobloxPlayControlStatus.NotFound,
    "A missing Roblox Play-control result was not recognized.");
Require(RobloxPlayControl.ParseResult("wrong-origin") == RobloxPlayControlStatus.WrongOrigin,
    "A wrong-origin Roblox Play-control result was not recognized.");
Require(RobloxPlayControl.ParseResult("arbitrary page text") == RobloxPlayControlStatus.Unknown,
    "Arbitrary WebView script output was treated as a valid Play-control result.");
Require(RobloxPlayControl.Script.Contains("location.hostname", StringComparison.Ordinal)
        && RobloxPlayControl.Script.Contains("roblox.com", StringComparison.OrdinalIgnoreCase)
        && RobloxPlayControl.Script.Contains("Play", StringComparison.Ordinal)
        && RobloxPlayControl.Script.Contains("window.open", StringComparison.Ordinal),
    "The Play-control script did not restrict itself to a trusted Roblox Play action.");
Require(RobloxPlayControl.TryParseCapturedLaunchUri(
            "\"roblox-player:1+gameinfo:script-hook-ticket\"",
            out var scriptHookUri)
        && scriptHookUri?.Scheme == "roblox-player"
        && !RobloxPlayControl.TryParseCapturedLaunchUri("\"https://www.roblox.com/games/123\"", out _),
    "The trusted-page capture fallback accepted an invalid scheme or rejected Roblox.");

var navigationGate = new RobloxNavigationGate();
navigationGate.CommitTopLevelNavigation(new Uri("https://www.roblox.com/games/123/Test"), succeeded: true);
Require(navigationGate.TryBeginLaunch(), "The navigation gate did not enter a pending launch state.");
var newWindowLaunch = RobloxNavigationCapturePolicy.Evaluate(
    navigationGate,
    new Uri("roblox-player:1+gameinfo:new-window-ticket"));
Require(newWindowLaunch?.Accepted == true,
    "A Roblox launch opened as a WebView new-window navigation was not captured.");

var resourceGate = new RobloxNavigationGate();
resourceGate.CommitTopLevelNavigation(new Uri("https://www.roblox.com/games/123/Test"), succeeded: true);
Require(resourceGate.TryBeginLaunch(), "The resource-route gate did not enter a pending launch state.");
var resourceDiagnostics = new List<string>();
var resourceLaunch = RobloxNavigationCapturePolicy.Evaluate(
    resourceGate,
    new Uri("roblox-player:1+gameinfo:resource-route-ticket"),
    "web-resource",
    resourceDiagnostics.Add);
Require(resourceLaunch?.Accepted == true
        && resourceDiagnostics.SequenceEqual(["macos-route: web-resource scheme=roblox-player outcome=accepted"]),
    "The WebView resource route was not captured with a redacted diagnostic description.");
var duplicateLaunch = RobloxNavigationCapturePolicy.Evaluate(
    resourceGate,
    new Uri("roblox-player:1+gameinfo:duplicate-route-ticket"),
    "new-window",
    resourceDiagnostics.Add);
Require(duplicateLaunch?.Accepted == false
        && duplicateLaunch.DiagnosticCode == "launch-not-pending"
        && resourceDiagnostics[^1] == "macos-route: new-window scheme=roblox-player outcome=rejected:launch-not-pending",
    "A duplicate WebView route consumed the launch twice or leaked its ticket.");

var duplicateAfterCaptureDiagnostic = RobloxNavigationCapturePolicy.DescribeRoute(
    "navigation-started",
    new Uri("roblox-player:1+gameinfo:duplicate-after-capture-ticket"),
    BrowserNavigationResult.Rejected("duplicate-after-capture"));
Require(duplicateAfterCaptureDiagnostic ==
        "macos-route: navigation-started scheme=roblox-player outcome=duplicate-after-capture",
    "A trailing WebView route was still described as a rejected launch instead of a handled duplicate.");

var routeTracker = new MacNavigationCaptureTracker();
var firstCapturedRoute = new Uri("roblox-player:1+gameinfo:first-captured-route");
var secondCapturedRoute = new Uri("roblox-player:1+gameinfo:second-captured-route");
routeTracker.RecordAccepted(firstCapturedRoute);
routeTracker.RecordAccepted(secondCapturedRoute);
Require(routeTracker.TryConsumeDuplicate(firstCapturedRoute)
        && !routeTracker.TryConsumeDuplicate(firstCapturedRoute)
        && routeTracker.TryConsumeDuplicate(secondCapturedRoute),
    "A delayed duplicate route was not correlated without consuming a later launch.");

var launchSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:captured-ticket"),
    clickAfterPolls: 2);
var launchStatuses = new List<RobloxPlayControlStatus>();
var launchCoordinator = new MacBrowserLaunchCoordinator(
    launchSession,
    launchStatuses.Add,
    TimeSpan.FromMilliseconds(1));
var capturedLaunchUri = await launchCoordinator.CaptureAsync(
    "account-id",
    new Uri("https://www.roblox.com/games/123/Test"),
    TimeSpan.FromSeconds(1));
Require(capturedLaunchUri.Scheme == "roblox-player"
        && launchSession.Events.SequenceEqual(["capture", "navigate", "script", "script"])
        && launchStatuses is [RobloxPlayControlStatus.NotFound, RobloxPlayControlStatus.Clicked],
    "The macOS launch coordinator did not capture after clicking Play in the expected order.");

var transientScriptSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:transient-script"),
    clickAfterPolls: 1,
    scriptFailures: 1);
var transientCapture = await new MacBrowserLaunchCoordinator(
        transientScriptSession,
        pollInterval: TimeSpan.FromMilliseconds(1))
    .CaptureAsync(
        "account-id",
        new Uri("https://www.roblox.com/games/123/Test"),
        TimeSpan.FromSeconds(1));
Require(transientCapture.Scheme == "roblox-player",
    "A transient WebView script failure prevented a later Play click.");

var transientOriginSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:transient-origin"),
    clickAfterPolls: int.MaxValue,
    scriptedResults: ["wrong-origin", "clicked"]);
var transientOriginCapture = await new MacBrowserLaunchCoordinator(
        transientOriginSession,
        pollInterval: TimeSpan.FromMilliseconds(1))
    .CaptureAsync(
        "account-id",
        new Uri("https://www.roblox.com/games/123/Test"),
        TimeSpan.FromSeconds(1));
Require(transientOriginCapture.Scheme == "roblox-player",
    "The initial WebView wrong-origin state incorrectly aborted a later Roblox Play click.");

var scriptHookSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:native-route-never-arrived"),
    clickAfterPolls: int.MaxValue,
    scriptedResults: ["clicked"],
    completeCaptureOnClick: false,
    capturedScriptUri: new Uri("roblox-player:1+gameinfo:script-hook-captured"));
var scriptHookCapture = await new MacBrowserLaunchCoordinator(
        scriptHookSession,
        pollInterval: TimeSpan.FromMilliseconds(1))
    .CaptureAsync(
        "account-id",
        new Uri("https://www.roblox.com/games/123/Test"),
        TimeSpan.FromSeconds(1));
Require(scriptHookCapture.AbsoluteUri.Contains("script-hook-captured", StringComparison.Ordinal)
        && scriptHookSession.Events.Contains("capture-script", StringComparer.Ordinal),
    "The trusted-page fallback did not recover a Roblox URI omitted by WKWebView routes.");

var schemeTimeoutSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:missing-scheme"),
    clickAfterPolls: int.MaxValue,
    scriptedResults: ["clicked"],
    completeCaptureOnClick: false);
try
{
    await new MacBrowserLaunchCoordinator(schemeTimeoutSession, pollInterval: TimeSpan.FromMilliseconds(1))
        .CaptureAsync(
            "account-id",
            new Uri("https://www.roblox.com/games/123/Test"),
            TimeSpan.FromMilliseconds(80));
    throw new InvalidOperationException("A missing macOS launch route unexpectedly completed.");
}
catch (TimeoutException exception)
{
    Require(exception.Message == "macos-launch-timeout-awaiting-scheme",
        "A post-click macOS timeout did not identify the missing custom-scheme handoff.");
}

var timeoutSession = new FakeMacBrowserLaunchSession(
    new Uri("roblox-player:1+gameinfo:never-captured"),
    clickAfterPolls: int.MaxValue);
try
{
    await new MacBrowserLaunchCoordinator(timeoutSession, pollInterval: TimeSpan.FromMilliseconds(1))
        .CaptureAsync(
            "account-id",
            new Uri("https://www.roblox.com/games/123/Test"),
            TimeSpan.FromMilliseconds(80));
    throw new InvalidOperationException("The macOS Play/capture timeout unexpectedly completed.");
}
catch (TimeoutException)
{
    Require(timeoutSession.CaptureCanceled,
        "A macOS Play/capture timeout did not cancel the pending browser capture.");
}

var rowBuildCount = 0;
var accountTemplate = AccountRailTemplatePolicy.CreateTemplate(candidate =>
{
    rowBuildCount++;
    return new TextBlock { Text = candidate.Label };
});
var renderedAccountRow = accountTemplate.Build(account, null);
Require(renderedAccountRow is TextBlock textBlock && textBlock.Text == account.Label && rowBuildCount == 1,
    "Account rail template did not delegate normal account rows to the row builder.");
var recycledAccountRow = accountTemplate.Build(null!, renderedAccountRow);
Require(recycledAccountRow is Border && rowBuildCount == 1,
    "Account rail template recycling did not produce a safe placeholder without invoking the row builder.");

Console.WriteLine("Desktop startup and preset policy tests passed.");

sealed class FakeMacBrowserLaunchSession(
    Uri launchUri,
    int clickAfterPolls,
    int scriptFailures = 0,
    IReadOnlyList<string>? scriptedResults = null,
    bool completeCaptureOnClick = true,
    Uri? capturedScriptUri = null) : IMacBrowserLaunchSession
{
    private readonly TaskCompletionSource<BrowserNavigationResult> _capture =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _polls;
    private int _scriptedResultIndex;
    private int _capturedScriptReads;

    public List<string> Events { get; } = [];
    public bool CaptureCanceled { get; private set; }

    public Task<BrowserNavigationResult> BeginLaunchCapture(string accountId, CancellationToken cancellationToken)
    {
        Events.Add("capture");
        cancellationToken.Register(() =>
        {
            CaptureCanceled = true;
            _capture.TrySetCanceled(cancellationToken);
        });
        return _capture.Task;
    }

    public ValueTask<BrowserNavigationResult> NavigateAsync(
        string accountId,
        Uri navigationUri,
        CancellationToken cancellationToken)
    {
        Events.Add("navigate");
        return ValueTask.FromResult(new BrowserNavigationResult(true, diagnosticCode: "navigation-started"));
    }

    public ValueTask<string> InvokeScriptAsync(
        string accountId,
        string script,
        CancellationToken cancellationToken)
    {
        Events.Add("script");
        if (string.Equals(script, RobloxPlayControl.CapturedLaunchUriScript, StringComparison.Ordinal))
        {
            Events.Add("capture-script");
            var captured = Interlocked.Increment(ref _capturedScriptReads) == 1
                ? capturedScriptUri?.AbsoluteUri ?? string.Empty
                : string.Empty;
            return ValueTask.FromResult(JsonSerializer.Serialize(captured));
        }

        if (Interlocked.Decrement(ref scriptFailures) >= 0)
        {
            throw new InvalidOperationException("document-not-ready");
        }

        if (scriptedResults is not null &&
            Interlocked.Increment(ref _scriptedResultIndex) <= scriptedResults.Count)
        {
            var scriptedResult = scriptedResults[_scriptedResultIndex - 1];
            if (completeCaptureOnClick && string.Equals(scriptedResult, "clicked", StringComparison.Ordinal))
            {
                _capture.TrySetResult(new BrowserNavigationResult(true, launchUri));
            }

            return ValueTask.FromResult(scriptedResult);
        }

        if (Interlocked.Increment(ref _polls) >= clickAfterPolls)
        {
            if (completeCaptureOnClick)
            {
                _capture.TrySetResult(new BrowserNavigationResult(true, launchUri));
            }
            return ValueTask.FromResult("clicked");
        }

        return ValueTask.FromResult("not-found");
    }
}
