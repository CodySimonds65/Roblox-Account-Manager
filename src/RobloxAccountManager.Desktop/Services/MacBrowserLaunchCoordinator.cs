using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Desktop.Services;

public interface IMacBrowserLaunchSession
{
    Task<BrowserNavigationResult> BeginLaunchCapture(string accountId, CancellationToken cancellationToken);

    ValueTask<BrowserNavigationResult> NavigateAsync(
        string accountId,
        Uri navigationUri,
        CancellationToken cancellationToken);

    ValueTask<string> InvokeScriptAsync(
        string accountId,
        string script,
        CancellationToken cancellationToken);
}

public sealed class MacBrowserLaunchCoordinator
{
    private readonly IMacBrowserLaunchSession _session;
    private readonly Action<RobloxPlayControlStatus>? _statusSink;
    private readonly TimeSpan _pollInterval;

    public MacBrowserLaunchCoordinator(
        IMacBrowserLaunchSession session,
        Action<RobloxPlayControlStatus>? statusSink = null,
        TimeSpan? pollInterval = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _statusSink = statusSink;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async ValueTask<Uri> CaptureAsync(
        string accountId,
        Uri navigationUri,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(navigationUri);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        using var launchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        launchTimeout.CancelAfter(timeout);
        var pending = _session.BeginLaunchCapture(accountId, launchTimeout.Token);
        var playClicked = false;
        try
        {
            await _session.NavigateAsync(accountId, navigationUri, launchTimeout.Token);
            while (true)
            {
                RobloxPlayControlStatus status;
                try
                {
                    status = RobloxPlayControl.ParseResult(await _session.InvokeScriptAsync(
                        accountId,
                        RobloxPlayControl.Script,
                        launchTimeout.Token));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    status = RobloxPlayControlStatus.Unknown;
                }

                _statusSink?.Invoke(status);
                if (status == RobloxPlayControlStatus.Clicked)
                {
                    playClicked = true;
                    break;
                }

                // NavigateAsync returns as soon as the WebView navigation is requested. On
                // macOS WKWebView can therefore report about:blank (or the previous document)
                // for one or more script polls before the Roblox page commits. Treat the
                // untrusted-origin result as transient during this bounded capture window;
                // the timeout remains the fail-closed boundary if the page never reaches Roblox.
                await Task.Delay(_pollInterval, launchTimeout.Token);
            }

            return await WaitForCapturedLaunchUriAsync(
                accountId,
                pending,
                launchTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (launchTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                playClicked ? "macos-launch-timeout-awaiting-scheme" : "macos-launch-timeout-awaiting-play",
                exception);
        }
        finally
        {
            launchTimeout.Cancel();
        }
    }

    private async ValueTask<Uri> WaitForCapturedLaunchUriAsync(
        string accountId,
        Task<BrowserNavigationResult> pending,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (pending.IsCompleted)
            {
                var navigation = await pending.ConfigureAwait(false);
                if (navigation.TryConsumeLaunchUri(out var routedUri) && routedUri is not null)
                    return routedUri;
                throw new InvalidOperationException("macos-launch-uri-not-captured");
            }

            try
            {
                var captured = await _session.InvokeScriptAsync(
                    accountId,
                    RobloxPlayControl.CapturedLaunchUriScript,
                    cancellationToken).ConfigureAwait(false);
                if (RobloxPlayControl.TryParseCapturedLaunchUri(captured, out var scriptUri)
                    && scriptUri is not null)
                {
                    return scriptUri;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // The native navigation routes remain authoritative. A transient script
                // failure is retried until the same bounded launch timeout expires.
            }

            await Task.WhenAny(pending, Task.Delay(_pollInterval, cancellationToken)).ConfigureAwait(false);
        }
    }
}
