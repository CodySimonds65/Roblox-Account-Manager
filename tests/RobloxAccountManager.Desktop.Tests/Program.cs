using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Core.Navigation;
using RobloxAccountManager.Desktop;
using RobloxAccountManager.Desktop.Services;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
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
        && RobloxPlayControl.Script.Contains("Play", StringComparison.Ordinal),
    "The Play-control script did not restrict itself to a trusted Roblox Play action.");

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

sealed class FakeMacBrowserLaunchSession(Uri launchUri, int clickAfterPolls, int scriptFailures = 0) : IMacBrowserLaunchSession
{
    private readonly TaskCompletionSource<BrowserNavigationResult> _capture =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _polls;

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
        if (Interlocked.Decrement(ref scriptFailures) >= 0)
        {
            throw new InvalidOperationException("document-not-ready");
        }

        if (Interlocked.Increment(ref _polls) >= clickAfterPolls)
        {
            _capture.TrySetResult(new BrowserNavigationResult(true, launchUri));
            return ValueTask.FromResult("clicked");
        }

        return ValueTask.FromResult("not-found");
    }
}
