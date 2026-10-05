# Windows UI previews

From the repository root on Windows with the .NET SDK:

```powershell
dotnet run --project build/WindowsUiPreview -- .
```

Writes PNGs to `artifacts/windows-ui-refresh/previews`. Checks the four main
screens at their default and minimum sizes, plus the smaller account, preset
and consent dialogs. The preview also checks shared window styling and that
the main launch controls fit.

Settings and compatibility use compiled dialog constructors with fixture data.
Main and Plugins use their source XAML with event handlers disconnected and
native hosts omitted. Catalog rows use fixtures matching the programmatic row
layout. No App startup, saved accounts, browser cookies, plugin execution or
Roblox process control is involved. These are offscreen layout previews, not
live screenshots or end-to-end input tests. Native title bars are excluded.
