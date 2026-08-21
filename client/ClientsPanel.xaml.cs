using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RobloxAltClient.Plugins;

namespace RobloxAltClient;

/// <summary>
/// Docks running game clients over a dedicated native viewport and presents
/// them as a tab strip. Roblox remains top-level for the human input path.
/// </summary>
public partial class ClientsPanel : UserControl
{
    private readonly Dictionary<string, DateTime> _embedTimes = new(StringComparer.Ordinal);
    private readonly Queue<string> _diagnosticLines = new();
    private readonly NativeInputDiagnostics _nativeInputDiagnostics = new();
    private readonly DispatcherTimer _diagnosticTimer;
    private readonly DispatcherTimer _relayoutTimer;
    private PluginRuntime? _runtime;
    private Window? _ownerWindow;
    private bool _attached;
    private bool _viewVisible;
    private bool _relayoutPending;

    public ClientsPanel()
    {
        InitializeComponent();
        _diagnosticTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        _diagnosticTimer.Tick += DiagnosticTimer_Tick;
        _relayoutTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _relayoutTimer.Tick += RelayoutTimer_Tick;
    }

    /// <summary>Raised on the UI thread when an account's tab is created for the first time.</summary>
    public event Action? AccountEmbedded;

    public void AttachToWindow(Window ownerWindow)
    {
        if (_attached) return;
        _attached = true;
        _ownerWindow = ownerWindow;
        _runtime = ((App)Application.Current).PluginRuntime;
        NativeClientHost.HandleCreated += NativeClientHost_HandleCreated;
        NativeClientHost.HandleDestroying += NativeClientHost_HandleDestroying;
        NativeClientHost.NativeSizeChanged += NativeClientHost_NativeSizeChanged;
        _runtime.Accounts.AccountChanged += Accounts_AccountChanged;
        _runtime.Accounts.AccountExited += Accounts_AccountExited;
        _runtime.ClientEmbeddings.FilterChanged += ResyncTabs;
        _runtime.ClientEmbeddings.Diagnostics = Log;
        ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
        ownerWindow.LocationChanged += OwnerWindow_LocationChanged;
        ownerWindow.StateChanged += OwnerWindow_StateChanged;
        ownerWindow.IsVisibleChanged += OwnerWindow_IsVisibleChanged;
        if (NativeClientHost.NativeHandle != nint.Zero)
            NativeClientHost_HandleCreated(NativeClientHost.NativeHandle);
        foreach (var account in _runtime.Accounts.Snapshot()) EnsureTab(account);
        Relayout();
        ShowOnlySelection();
    }

    public void Detach()
    {
        if (_runtime is not null)
        {
            _runtime.Accounts.AccountChanged -= Accounts_AccountChanged;
            _runtime.Accounts.AccountExited -= Accounts_AccountExited;
            _runtime.ClientEmbeddings.FilterChanged -= ResyncTabs;
            _runtime.ClientEmbeddings.Diagnostics = null;
        }
        if (_ownerWindow is not null) _ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
        if (_ownerWindow is not null)
        {
            _ownerWindow.LocationChanged -= OwnerWindow_LocationChanged;
            _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
            _ownerWindow.IsVisibleChanged -= OwnerWindow_IsVisibleChanged;
        }
        NativeClientHost.HandleCreated -= NativeClientHost_HandleCreated;
        NativeClientHost.HandleDestroying -= NativeClientHost_HandleDestroying;
        NativeClientHost.NativeSizeChanged -= NativeClientHost_NativeSizeChanged;
        if (_runtime is not null && NativeClientHost.NativeHandle != nint.Zero)
            _runtime.ClientEmbeddings.ReleaseHostWindow(NativeClientHost.NativeHandle);
        _diagnosticTimer.Stop();
        _relayoutTimer.Stop();
        _relayoutPending = false;
        _attached = false;
    }

    public void SetViewVisible(bool visible)
    {
        if (_runtime is null) return;
        _viewVisible = visible;
        if (visible)
        {
            ShowOnlySelection();
            Relayout();
        }
        else
        {
            _runtime.ClientEmbeddings.HideAll();
        }
    }

    public bool IsSelected(string accountId) =>
        ClientTabs.SelectedItem is ManagedAccountSnapshot selected && selected.AccountId == accountId;

    private void EnsureTab(ManagedAccountSnapshot account)
    {
        if (_runtime is null) return;
        if (_runtime.Accounts.IsStopping(account.AccountId)) return;
        if (_runtime.ClientEmbeddings.EmbedFilter?.Invoke(account.AccountId) == false)
        {
            _runtime.ClientEmbeddings.TryUnembed(account.AccountId);
            RemoveTab(account.AccountId);
            ShowOnlySelection();
            Relayout();
            return;
        }
        var root = account.RootWindowHandle != nint.Zero ? account.RootWindowHandle : account.WindowHandle;
        // Dock only once the client is fully stable: never hide, restyle, or
        // resize a D3D window during its startup handshake — games crash. The
        // window must be several seconds old and a real size.
        var processAgeSeconds = account.ProcessStartTimeUtcTicks > 0
            ? (DateTime.UtcNow - new DateTime(account.ProcessStartTimeUtcTicks, DateTimeKind.Utc)).TotalSeconds
            : 0;
        var embedReady = root != nint.Zero && processAgeSeconds >= 4 &&
                         account.ClientWidth >= 400 && account.ClientHeight >= 300;
        if (embedReady)
        {
            var hostIntegrity = ProcessIntegrity.Current;
            var clientIntegrity = ProcessIntegrity.ForWindow(root);
            if (hostIntegrity != ProcessIntegrityLevel.Unknown &&
                clientIntegrity != ProcessIntegrityLevel.Unknown && hostIntegrity != clientIntegrity)
                Log($"{account.Label}: RAM integrity is {hostIntegrity}; Roblox is {clientIntegrity}. Native input remains OS-routed.");

            _runtime.ClientEmbeddings.TryEmbed(
                account.AccountId,
                root,
                account.ProcessId,
                account.ProcessStartTimeUtcTicks,
                "RobloxPlayerBeta",
                account.WindowHandle);
            if (_runtime.ClientEmbeddings.IsEmbedded(account.AccountId))
            {
                _embedTimes[account.AccountId] = DateTime.UtcNow;
                if (!_viewVisible) _runtime.ClientEmbeddings.HideAll();
                else ShowOnlySelection();
            }
        }
        if (HasTab(account.AccountId))
        {
            Relayout();
            return;
        }
        ClientTabs.Items.Add(account);
        if (ClientTabs.SelectedItem is null) ClientTabs.SelectedIndex = 0;
        AccountEmbedded?.Invoke();
        ShowOnlySelection();
        Relayout();
    }

    private void Accounts_AccountChanged(object? sender, ManagedAccountSnapshot snapshot)
    {
        if (Dispatcher.CheckAccess()) EnsureTab(snapshot);
        else Dispatcher.BeginInvoke(new Action(() => EnsureTab(snapshot)));
    }

    private void Accounts_AccountExited(object? sender, ManagedAccountSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Accounts_AccountExited(sender, snapshot)));
            return;
        }
        if (_runtime is null) return;
        _runtime.ClientEmbeddings.TryUnembed(snapshot.AccountId);
        if (_embedTimes.TryGetValue(snapshot.AccountId, out var embeddedAt) &&
            (DateTime.UtcNow - embeddedAt).TotalSeconds < 10)
        {
            _embedTimes.Remove(snapshot.AccountId);
            Log($"Client {snapshot.Label} exited {(DateTime.UtcNow - embeddedAt).TotalSeconds:0.0}s after embedding.");
        }
        RemoveTab(snapshot.AccountId);
        ShowOnlySelection();
        Relayout();
    }

    private void ResyncTabs()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ResyncTabs));
            return;
        }
        if (_runtime is null) return;
        foreach (var account in _runtime.Accounts.Snapshot())
        {
            if (_runtime.ClientEmbeddings.EmbedFilter?.Invoke(account.AccountId) == true)
            {
                EnsureTab(account);
            }
            else
            {
                _runtime.ClientEmbeddings.TryUnembed(account.AccountId);
                RemoveTab(account.AccountId);
            }
        }
        ShowOnlySelection();
        Relayout();
    }

    private void ClientTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtime is null) return;
        if (ClientTabs.SelectedItem is not ManagedAccountSnapshot selected) return;
        _runtime.ClientEmbeddings.ShowOnly(selected.AccountId);
        Relayout();
    }

    private async void CloseClientTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_runtime is null || sender is not FrameworkElement { DataContext: ManagedAccountSnapshot account }) return;

        // Closing a tab is a session operation, not an account-profile delete.
        // Remove the native embedding before asking the registry to stop the
        // identity-checked Roblox process so its final exit cannot leave a
        // hidden tray client behind.
        var termination = _runtime.Accounts.TerminateAccountAsync(account.AccountId);
        _runtime.ClientEmbeddings.TryUnembed(account.AccountId);
        try
        {
            var stopped = await termination;
            if (!stopped)
                Log($"RAM could not close {account.Label}; the client remains running.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            Log($"RAM could not close {account.Label}: {ex.Message}");
        }
    }

    private bool HasTab(string accountId) =>
        ClientTabs.Items.OfType<ManagedAccountSnapshot>().Any(item => item.AccountId == accountId);

    private void RemoveTab(string accountId)
    {
        var tab = ClientTabs.Items.OfType<ManagedAccountSnapshot>().FirstOrDefault(item => item.AccountId == accountId);
        if (tab is not null) ClientTabs.Items.Remove(tab);
    }

    private void ShowOnlySelection()
    {
        if (_runtime is null || !_viewVisible) return;
        if (ClientTabs.SelectedItem is ManagedAccountSnapshot selected)
        {
            _runtime.ClientEmbeddings.ShowOnly(selected.AccountId);
            return;
        }
        if (ClientTabs.Items.Count > 0)
        {
            ClientTabs.SelectedIndex = 0;
            if (ClientTabs.SelectedItem is ManagedAccountSnapshot first)
                _runtime.ClientEmbeddings.ShowOnly(first.AccountId);
        }
    }

    private void NativeClientHost_HandleCreated(nint hostWindow)
    {
        if (_runtime is null) return;
        _runtime.ClientEmbeddings.SetHostWindow(hostWindow);
        foreach (var account in _runtime.Accounts.Snapshot()) EnsureTab(account);
        ShowOnlySelection();
        Relayout();
    }

    private void NativeClientHost_HandleDestroying(nint hostWindow) =>
        _runtime?.ClientEmbeddings.ReleaseHostWindow(hostWindow);

    private void NativeClientHost_NativeSizeChanged() => Relayout();

    private void Relayout()
    {
        if (NativeClientHost.NativeHandle == nint.Zero || !IsLoaded || Visibility != Visibility.Visible) return;
        _relayoutPending = true;
        if (!_relayoutTimer.IsEnabled) _relayoutTimer.Start();
    }

    private void RelayoutTimer_Tick(object? sender, EventArgs e)
    {
        if (!_relayoutPending || NativeClientHost.NativeHandle == nint.Zero || !IsLoaded || Visibility != Visibility.Visible)
        {
            _relayoutPending = false;
            _relayoutTimer.Stop();
            return;
        }

        _relayoutPending = false;
        _runtime?.ClientEmbeddings.Layout();
        if (!_relayoutPending) _relayoutTimer.Stop();
    }

    private void ClientsPanel_Loaded(object sender, RoutedEventArgs e) => Relayout();

    private void HostArea_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();
    private void OwnerWindow_LocationChanged(object? sender, EventArgs e) => Relayout();

    private void OwnerWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_runtime is null) return;
        if (_ownerWindow?.WindowState == WindowState.Minimized)
        {
            _runtime.ClientEmbeddings.HideAll();
            return;
        }
        if (_viewVisible)
        {
            ShowOnlySelection();
            Relayout();
        }
    }

    private void OwnerWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_runtime is null) return;
        if (e.NewValue is bool visible && visible)
        {
            if (_viewVisible)
            {
                ShowOnlySelection();
                Relayout();
            }
        }
        else
        {
            _runtime.ClientEmbeddings.HideAll();
        }
    }

    private void NativeDiagnosticsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (NativeDiagnosticsToggle.IsChecked == true)
        {
            _diagnosticTimer.Start();
            Log("Native diagnostics enabled; no input is hooked, forwarded, or synthesized.");
        }
        else
        {
            _diagnosticTimer.Stop();
            _diagnosticLines.Clear();
            FocusDiagnostics.Text = string.Empty;
            FocusDiagnostics.Visibility = Visibility.Collapsed;
        }
    }

    private void DiagnosticTimer_Tick(object? sender, EventArgs e)
    {
        if (!_viewVisible || _runtime is null || NativeClientHost.NativeHandle == nint.Zero) return;
        var accountId = _runtime.ClientEmbeddings.VisibleAccountId;
        if (accountId is null) return;
        var root = _runtime.ClientEmbeddings.RootFor(accountId);
        if (root is null || root == nint.Zero) return;
        var snapshot = _nativeInputDiagnostics.CaptureAfterSystemInput(NativeClientHost.NativeHandle, root.Value);
        if (snapshot is not null) Log(snapshot);
    }

    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        if (NativeDiagnosticsToggle.IsChecked != true) return;
        while (_diagnosticLines.Count >= 4) _diagnosticLines.Dequeue();
        _diagnosticLines.Enqueue(message);
        FocusDiagnostics.Text = string.Join(Environment.NewLine, _diagnosticLines);
        FocusDiagnostics.Visibility = Visibility.Visible;
    }
}
