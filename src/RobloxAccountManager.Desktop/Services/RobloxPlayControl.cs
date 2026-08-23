using System.Text.Json;
using RobloxAccountManager.Core.Navigation;

namespace RobloxAccountManager.Desktop.Services;

public enum RobloxPlayControlStatus
{
    Unknown,
    NotFound,
    WrongOrigin,
    Clicked
}

public static class RobloxPlayControl
{
    public const string Script = """
        (() => {
          const host = String(location.hostname || '').toLowerCase();
          if (!(host === 'roblox.com' || host.endsWith('.roblox.com'))) return 'wrong-origin';
          const captureKey = '__robloxAccountManagerLaunchUri';
          const captureScheme = value => {
            const candidate = String(value || '');
            if (!/^roblox(?:-player)?:/i.test(candidate)) return false;
            window[captureKey] = candidate;
            return true;
          };
          if (!window.__robloxAccountManagerCaptureInstalled) {
            const originalOpen = window.open;
            window.open = function(url, ...args) {
              if (captureScheme(url)) return null;
              return originalOpen.call(this, url, ...args);
            };
            const originalAnchorClick = HTMLAnchorElement.prototype.click;
            HTMLAnchorElement.prototype.click = function(...args) {
              if (captureScheme(this.href)) return;
              return originalAnchorClick.apply(this, args);
            };
            document.addEventListener('click', event => {
              const target = event.target instanceof Element ? event.target.closest('a[href]') : null;
              if (!target || !captureScheme(target.href)) return;
              event.preventDefault();
              event.stopImmediatePropagation();
            }, true);
            window.__robloxAccountManagerCaptureInstalled = true;
          }
          const selectors = [
            'button[data-testid="play-button"]',
            '[data-testid="play-button"]',
            '#play-button',
            'button[aria-label="Play"]',
            '[role="button"][aria-label="Play"]'
          ];
          let control = null;
          for (const selector of selectors) {
            control = document.querySelector(selector);
            if (control) break;
          }
          if (!control) {
            const candidates = document.querySelectorAll('button,[role="button"]');
            control = Array.from(candidates).find(candidate => {
              const label = String(candidate.getAttribute('aria-label') || '').trim().toLowerCase();
              const text = String(candidate.textContent || '').trim().toLowerCase();
              return label === 'play' || text === 'play';
            }) || null;
          }
          if (!control || control.disabled || control.getAttribute('aria-disabled') === 'true') return 'not-found';
          const rect = control.getBoundingClientRect();
          const style = window.getComputedStyle(control);
          if (rect.width <= 0 || rect.height <= 0 || style.display === 'none' || style.visibility === 'hidden') return 'not-found';
          control.click();
          return 'clicked';
        })()
        """;

    public const string CapturedLaunchUriScript = """
        (() => {
          const host = String(location.hostname || '').toLowerCase();
          if (!(host === 'roblox.com' || host.endsWith('.roblox.com'))) return '';
          const captureKey = '__robloxAccountManagerLaunchUri';
          const captured = String(window[captureKey] || '');
          window[captureKey] = '';
          return captured;
        })()
        """;

    public static RobloxPlayControlStatus ParseResult(string? value)
    {
        var normalized = value?.Trim().Trim('"').ToLowerInvariant();
        return normalized switch
        {
            "clicked" => RobloxPlayControlStatus.Clicked,
            "not-found" => RobloxPlayControlStatus.NotFound,
            "wrong-origin" => RobloxPlayControlStatus.WrongOrigin,
            _ => RobloxPlayControlStatus.Unknown
        };
    }

    public static bool TryParseCapturedLaunchUri(string? value, out Uri? launchUri)
    {
        launchUri = null;
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.StartsWith('"') && normalized.EndsWith('"'))
        {
            try
            {
                normalized = JsonSerializer.Deserialize<string>(normalized);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var candidate)
            && RobloxNavigationGate.IsRobloxScheme(candidate))
        {
            launchUri = candidate;
            return true;
        }

        launchUri = null;
        return false;
    }
}
