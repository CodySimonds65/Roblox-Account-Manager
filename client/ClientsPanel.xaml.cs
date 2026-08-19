using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RobloxAltClient.Plugins;

namespace RobloxAltClient;

/// <summary>
/// Embeds running game clients as child windows of the host window and presents
/// them as a tab strip. The native children live in the owner window's HWND, so
/// all layout coordinates are translated to the owner window's client space.
/// </summary>
public partial class ClientsPanel : UserControl
{
    private readonly ObservableCollection<string> _focusDiagnostics = new();
    private readonly Dictionary<string, DateTime> _embedTimes = new(StringComparer.Ordinal);
    private PluginRuntime? _runtime;
    private Window? _ownerWindow;
    private nint _hostWindow;
    private bool _attached;
    private bool _viewVisible;

    public ClientsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised on the UI thread when an account's tab is created for the first time.</summary>
    public event Action? AccountEmbedded;

    public void AttachToWindow(Window ownerWindow)
    {
        if (_attached) return;
        _attached = true;
        _ownerWindow = ownerWindow;
        _runtime = ((App)Application.Current).PluginRuntime;
        _hostWindow = new WindowInteropHelper(ownerWindow).Handle;
        _runtime.ClientEmbeddings.SetHostWindow(_hostWindow);
        EmbeddedInputBridge.Diagnostics = Log;
        EmbeddedInputBridge.Attach(_hostWindow, () => _runtime?.ClientEmbeddings.VisibleAccountId,
            accountId => _runtime?.ClientEmbeddings.RootFor(accountId));
        _runtime.Accounts.AccountChanged += Accounts_AccountChanged;
        _runtime.Accounts.AccountExited += Accounts_AccountExited;
        _runtime.ClientEmbeddings.FilterChanged += ResyncTabs;
        ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
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
        }
        if (_ownerWindow is not null) _ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
        EmbeddedInputBridge.Diagnostics = null;
        EmbeddedInputBridge.Detach();
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
        if (_runtime.ClientEmbeddings.EmbedFilter?.Invoke(account.AccountId) == false)
        {
            _runtime.ClientEmbeddings.TryUnembed(account.AccountId);
            RemoveTab(account.AccountId);
            ShowOnlySelection();
            Relayout();
            return;
        }
        var root = account.RootWindowHandle != nint.Zero ? account.RootWindowHandle : account.WindowHandle;
        // Embed only once the client is fully stable: never hide, reparent, or
        // resize a D3D window during its startup handshake — games crash. The
        // window must be several seconds old and a real size.
        var processAgeSeconds = account.ProcessStartTimeUtcTicks > 0
            ? (DateTime.UtcNow - new DateTime(account.ProcessStartTimeUtcTicks, DateTimeKind.Utc)).TotalSeconds
            : 0;
        var embedReady = root != nint.Zero && processAgeSeconds >= 4 &&
                         account.ClientWidth >= 400 && account.ClientHeight >= 300;
        if (embedReady)
        {
            _runtime.ClientEmbeddings.TryEmbed(account.AccountId, root);
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
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_runtime?.ClientEmbeddings.HostOwnsForeground() != true ||
                !_runtime.ClientEmbeddings.IsVisible(selected.AccountId)) return;
            var root = _runtime?.ClientEmbeddings.RootFor(selected.AccountId);
            if (root is not null && root != nint.Zero) EmbeddedInputBridge.FocusEmbedded(root.Value);
        }));
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

    private (int Left, int Top, int Width, int Height) HostRect()
    {
        var reference = (UIElement?)_ownerWindow ?? this;
        var topLeft = HostArea.TranslatePoint(new Point(0, 0), reference);
        var dpi = VisualTreeHelper.GetDpi(this);
        return (
            (int)Math.Round(Math.Max(0, topLeft.X) * dpi.DpiScaleX),
            (int)Math.Round(Math.Max(0, topLeft.Y) * dpi.DpiScaleY),
            (int)Math.Max(1, Math.Round(HostArea.ActualWidth * dpi.DpiScaleX)),
            (int)Math.Max(1, Math.Round(HostArea.ActualHeight * dpi.DpiScaleY)));
    }

    private void Relayout()
    {
        if (_hostWindow == nint.Zero || !IsLoaded || Visibility != Visibility.Visible || HostArea.ActualWidth < 100) return;
        var rect = HostRect();
        _runtime?.ClientEmbeddings.Layout(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private void ClientsPanel_Loaded(object sender, RoutedEventArgs e) => Relayout();

    private void HostArea_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }
        if (_focusDiagnostics.Count >= 12) _focusDiagnostics.RemoveAt(0);
        _focusDiagnostics.Add(message);
        FocusDiagnostics.Text = string.Join(Environment.NewLine, _focusDiagnostics);
        FocusDiagnostics.Visibility = Visibility.Visible;
    }
}
