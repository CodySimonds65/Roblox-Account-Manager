using Avalonia.Controls;
using Avalonia.Platform;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Navigation;

namespace RobloxAccountManager.Desktop.Services;

public sealed class UnsupportedWebsiteDataStoreRemover : IAccountBrowserDataStoreRemover
{
    public bool IsSupported => false;

    public ValueTask RemoveAsync(Guid identifier, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException(
            "Exact website-data-store deletion is not available on this platform (platform-not-supported)."));
}

/// <summary>
/// Owns one NativeWebView and one persistent store identity per account. Views
/// must be detached from their visual parent before RemoveAsync is called.
/// </summary>
public sealed class AvaloniaAccountBrowserSessionService : IAccountBrowserSessionService, IMacBrowserLaunchSession
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly string _windowsDataDirectory;
    private readonly IAccountBrowserDataStoreRemover _storeRemover;

    public AvaloniaAccountBrowserSessionService(
        IAccountBrowserDataStoreRemover? storeRemover = null,
        string? windowsDataDirectory = null)
    {
        _storeRemover = storeRemover ?? new UnsupportedWebsiteDataStoreRemover();
        _windowsDataDirectory = windowsDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient", "WebView2");
    }

    public NativeWebView GetView(string accountId) =>
        _sessions.TryGetValue(accountId, out var session)
            ? session.View
            : throw new KeyNotFoundException("The account browser session has not been created.");

    public bool HasSession(string accountId) => _sessions.ContainsKey(accountId);

    public Task<BrowserNavigationResult> BeginLaunchCapture(string accountId, CancellationToken cancellationToken)
    {
        var session = GetSession(accountId);
        if (Volatile.Read(ref session.PendingLaunch) is not null)
            throw new InvalidOperationException("A browser launch is already pending for this account.");
        if (!session.Gate.TryBeginLaunch())
            throw new InvalidOperationException("A browser launch is already pending for this account.");
        var pending = new TaskCompletionSource<BrowserNavigationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref session.PendingLaunch, pending, null) is not null)
        {
            session.Gate.CancelPendingLaunch();
            throw new InvalidOperationException("A browser launch is already pending for this account.");
        }
        var registration = cancellationToken.Register(() =>
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref session.PendingLaunch, null, pending), pending))
            {
                session.Gate.CancelPendingLaunch();
                pending.TrySetCanceled(cancellationToken);
            }
        });
        _ = pending.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return pending.Task;
    }

    public ValueTask<BrowserSessionDescriptor> CreateAsync(
        string accountId, string profileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(accountId, "N", out var dataStoreIdentifier) && !Guid.TryParse(accountId, out dataStoreIdentifier))
            throw new ArgumentException("Account ids must be GUIDs so website stores remain stable.", nameof(accountId));
        if (_sessions.TryGetValue(accountId, out var existing)) return ValueTask.FromResult(existing.Descriptor);

        var platform = OperatingSystem.IsMacOS() ? RobloxPlatform.MacOS :
            OperatingSystem.IsWindows() ? RobloxPlatform.Windows : RobloxPlatform.Unknown;
        var view = new NativeWebView();
        var gate = new RobloxNavigationGate();
        view.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.ProfileName = accountId;
                windows.UserDataFolder = _windowsDataDirectory;
                windows.IsInPrivateModeEnabled = false;
            }
            else if (args is AppleWKWebViewEnvironmentRequestedEventArgs apple)
            {
                apple.NonPersistentDataStore = false;
                apple.DataStoreIdentifier = dataStoreIdentifier;
                apple.UpgradeKnownHostsToHTTPS = true;
            }
        };
        var descriptor = new BrowserSessionDescriptor(accountId, profileName, dataStoreIdentifier.ToString("D"), platform);
        var session = new Session(descriptor, view, gate, dataStoreIdentifier);
        view.NavigationCompleted += (_, args) => gate.CommitTopLevelNavigation(args.Request, args.IsSuccess);
        view.NavigationStarted += (_, args) =>
        {
            var result = RobloxNavigationCapturePolicy.Evaluate(gate, args.Request);
            if (result is null) return;
            args.Cancel = true;
            if (result.Accepted)
            {
                var pending = Interlocked.Exchange(ref session.PendingLaunch, null);
                pending?.TrySetResult(result);
            }
        };
        view.NewWindowRequested += (_, args) =>
        {
            var result = RobloxNavigationCapturePolicy.Evaluate(gate, args.Request);
            if (result is null) return;
            args.Handled = true;
            if (result.Accepted)
            {
                var pending = Interlocked.Exchange(ref session.PendingLaunch, null);
                pending?.TrySetResult(result);
            }
        };
        _sessions.Add(accountId, session);
        return ValueTask.FromResult(descriptor);
    }

    public ValueTask<BrowserNavigationResult> NavigateAsync(
        string accountId, Uri navigationUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!navigationUri.IsAbsoluteUri || navigationUri.Scheme is not ("https" or "http"))
            return ValueTask.FromResult(BrowserNavigationResult.Rejected("unsupported-navigation-scheme"));
        GetSession(accountId).View.Navigate(navigationUri);
        return ValueTask.FromResult(new BrowserNavigationResult(true, diagnosticCode: "navigation-started"));
    }

    public async ValueTask<string> InvokeScriptAsync(
        string accountId,
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        cancellationToken.ThrowIfCancellationRequested();
        return await GetSession(accountId).View.InvokeScript(script).WaitAsync(cancellationToken) ?? string.Empty;
    }

    public async ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (!_storeRemover.IsSupported)
            throw new PlatformNotSupportedException(
                "Exact account browser store deletion is unavailable (platform-not-supported).");
        if (!_sessions.Remove(accountId, out var session)) return;
        session.Gate.CancelPendingLaunch();
        Interlocked.Exchange(ref session.PendingLaunch, null)?.TrySetCanceled(cancellationToken);
        await ReleaseViewAsync(session.View, cancellationToken).ConfigureAwait(false);
        if (session.Descriptor.Platform == RobloxPlatform.MacOS)
            await _storeRemover.RemoveAsync(session.DataStoreIdentifier, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (_sessions.Remove(accountId, out var session))
        {
            session.Gate.CancelPendingLaunch();
            Interlocked.Exchange(ref session.PendingLaunch, null)?.TrySetCanceled(cancellationToken);
            await ReleaseViewAsync(session.View, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask ReleaseViewAsync(NativeWebView view, CancellationToken cancellationToken)
    {
        var hadAdapter = view.TryGetPlatformHandle() is not null;
        var destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void AdapterDestroyed(object? sender, WebViewAdapterEventArgs args) => destroyed.TrySetResult();
        view.AdapterDestroyed += AdapterDestroyed;
        try
        {
            view.Stop();
            switch (view.Parent)
            {
                case Panel panel:
                    panel.Children.Remove(view);
                    break;
                case ContentControl content when ReferenceEquals(content.Content, view):
                    content.Content = null;
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, view):
                    decorator.Child = null;
                    break;
            }

            if (hadAdapter)
                await destroyed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            view.AdapterDestroyed -= AdapterDestroyed;
        }
    }

    private Session GetSession(string accountId) =>
        _sessions.TryGetValue(accountId, out var session)
            ? session
            : throw new KeyNotFoundException("The account browser session has not been created.");

    private sealed class Session
    {
        public Session(BrowserSessionDescriptor descriptor, NativeWebView view, RobloxNavigationGate gate, Guid dataStoreIdentifier)
        {
            Descriptor = descriptor;
            View = view;
            Gate = gate;
            DataStoreIdentifier = dataStoreIdentifier;
        }

        public BrowserSessionDescriptor Descriptor { get; }
        public NativeWebView View { get; }
        public RobloxNavigationGate Gate { get; }
        public Guid DataStoreIdentifier { get; }
        public TaskCompletionSource<BrowserNavigationResult>? PendingLaunch;
    }
}
