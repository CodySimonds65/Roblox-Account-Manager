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
}
