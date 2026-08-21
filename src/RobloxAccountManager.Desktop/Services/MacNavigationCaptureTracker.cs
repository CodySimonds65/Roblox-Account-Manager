using System.Security.Cryptography;
using System.Text;

namespace RobloxAccountManager.Desktop.Services;

/// <summary>
/// Correlates the duplicate protocol-route notifications emitted by WKWebView
/// without retaining the ticket-bearing URI itself. A route accepted by one
/// launch remains recognizable until its trailing duplicate notification is
/// consumed, even if a later launch has already been armed.
/// </summary>
public sealed class MacNavigationCaptureTracker
{
    private const int MaximumRememberedRoutes = 32;
    private readonly object _sync = new();
    private readonly HashSet<string> _acceptedRoutes = new(StringComparer.Ordinal);

    public void RecordAccepted(Uri request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(request);
        lock (_sync)
        {
            if (_acceptedRoutes.Count >= MaximumRememberedRoutes)
                _acceptedRoutes.Remove(_acceptedRoutes.First());
            _acceptedRoutes.Add(fingerprint);
        }
    }

    public bool TryConsumeDuplicate(Uri request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(request);
        lock (_sync)
        {
            return _acceptedRoutes.Remove(fingerprint);
        }
    }

    private static string Fingerprint(Uri request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.IsAbsoluteUri ? request.AbsoluteUri : request.ToString())));
}
