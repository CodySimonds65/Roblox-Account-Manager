using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Desktop;

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
