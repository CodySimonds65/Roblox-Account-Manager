using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RobloxAltClient.Plugins;

namespace RobloxAltClient;

public partial class ClientsWindow : Window
{
    private PluginRuntime? _runtime;
    private nint _hostWindow;

    public ClientsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Relayout();
        SourceInitialized += (_, _) =>
        {
            _hostWindow = new WindowInteropHelper(this).Handle;
            _runtime = ((App)Application.Current).PluginRuntime;
            _runtime.ClientEmbeddings.SetHostWindow(_hostWindow);
            _runtime.ClientEmbeddings.EmbeddedRootResolver = ResolveEmbeddedRoot;
            _runtime.ClientEmbeddings.EmbeddedActivate = ActivateEmbedded;
            _runtime.Accounts.AccountChanged += Accounts_AccountChanged;
            _runtime.Accounts.AccountExited += Accounts_AccountExited;
            _runtime.ClientEmbeddings.FilterChanged += ResyncTabs;
            foreach (var account in _runtime.Accounts.Snapshot()) EnsureTab(account);
            Relayout();
        };
    }

    private nint? ResolveEmbeddedRoot(string accountId)
    {
        if (_runtime is null) return null;
        return _runtime.ClientEmbeddings.RootFor(accountId);
    }

    private void ActivateEmbedded(string accountId)
    {
        if (Dispatcher.CheckAccess()) ActivateEmbeddedCore(accountId);
        else Dispatcher.Invoke(new Action(() => ActivateEmbeddedCore(accountId)));
    }

    private void ActivateEmbeddedCore(string accountId)
    {
        SelectTab(accountId);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        _runtime?.ClientEmbeddings.Focus(accountId);
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
        _runtime?.ClientEmbeddings.TryUnembed(snapshot.AccountId);
        var existing = ClientTabs.Items.OfType<ManagedAccountSnapshot>().FirstOrDefault(item => item.AccountId == snapshot.AccountId);
        if (existing is not null)
        {
            ClientTabs.Items.Remove(existing);
            ShowOnlySelectionOrFirst();
        }
        Relayout();
    }

    private void EnsureTab(ManagedAccountSnapshot account)
    {
        if (_runtime is null) return;
        var root = account.RootWindowHandle != nint.Zero ? account.RootWindowHandle : account.WindowHandle;
        if (_runtime.ClientEmbeddings.EmbedFilter?.Invoke(account.AccountId) == false)
        {
            _runtime.ClientEmbeddings.TryUnembed(account.AccountId);
            var filtered = ClientTabs.Items.OfType<ManagedAccountSnapshot>().FirstOrDefault(item => item.AccountId == account.AccountId);
            if (filtered is not null)
            {
                ClientTabs.Items.Remove(filtered);
                ShowOnlySelectionOrFirst();
            }
            Relayout();
            return;
        }
        if (root != nint.Zero) _runtime.ClientEmbeddings.TryEmbed(account.AccountId, root);
        if (ClientTabs.Items.OfType<ManagedAccountSnapshot>().Any(item => item.AccountId == account.AccountId)) return;
        ClientTabs.Items.Add(account);
        if (ClientTabs.SelectedItem is null) ClientTabs.SelectedIndex = 0;
        var selected = ClientTabs.SelectedItem as ManagedAccountSnapshot ?? account;
        _runtime.ClientEmbeddings.ShowOnly(selected.AccountId);
        Relayout();
    }

    private void ShowOnlySelectionOrFirst()
    {
        if (ClientTabs.SelectedItem is ManagedAccountSnapshot current)
        {
            _runtime?.ClientEmbeddings.ShowOnly(current.AccountId);
            return;
        }
        if (ClientTabs.Items.Count > 0)
        {
            ClientTabs.SelectedIndex = 0;
            if (ClientTabs.SelectedItem is ManagedAccountSnapshot first)
                _runtime?.ClientEmbeddings.ShowOnly(first.AccountId);
        }
    }

    private void ResyncTabs()
    {
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
                var filtered = ClientTabs.Items.OfType<ManagedAccountSnapshot>().FirstOrDefault(item => item.AccountId == account.AccountId);
                if (filtered is not null)
                {
                    ClientTabs.Items.Remove(filtered);
                    ShowOnlySelectionOrFirst();
                }
            }
        }
        if (ClientTabs.SelectedItem is null && ClientTabs.Items.Count > 0) ClientTabs.SelectedIndex = 0;
        if (ClientTabs.SelectedItem is ManagedAccountSnapshot current)
            _runtime.ClientEmbeddings.ShowOnly(current.AccountId);
        Relayout();
    }

    private void SelectTab(string accountId)
    {
        for (var i = 0; i < ClientTabs.Items.Count; i++)
        {
            if (ClientTabs.Items[i] is ManagedAccountSnapshot item && item.AccountId == accountId)
            {
                ClientTabs.SelectedIndex = i;
                return;
            }
        }
    }

    private void ClientTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ClientTabs.SelectedItem as ManagedAccountSnapshot;
        if (selected is null) return;
        _runtime?.ClientEmbeddings.ShowOnly(selected.AccountId);
        _runtime?.ClientEmbeddings.Focus(selected.AccountId);
        Relayout();
    }

    private void HostArea_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private (int Left, int Top, int Width, int Height) HostRect()
    {
        var topLeft = HostArea.TranslatePoint(new Point(0, 0), this);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY;
        return (
            (int)Math.Round(Math.Max(0, topLeft.X) * scaleX),
            (int)Math.Round(Math.Max(0, topLeft.Y) * scaleY),
            (int)Math.Max(1, Math.Round(HostArea.ActualWidth * scaleX)),
            (int)Math.Max(1, Math.Round(HostArea.ActualHeight * scaleY)));
    }

    private void Relayout()
    {
        if (_hostWindow == nint.Zero || !IsLoaded) return;
        var rect = HostRect();
        _runtime?.ClientEmbeddings.Layout(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_runtime is not null)
        {
            _runtime.Accounts.AccountChanged -= Accounts_AccountChanged;
            _runtime.Accounts.AccountExited -= Accounts_AccountExited;
            _runtime.ClientEmbeddings.FilterChanged -= ResyncTabs;
            _runtime.ClientEmbeddings.EmbeddedRootResolver = null;
            _runtime.ClientEmbeddings.EmbeddedActivate = null;
            _runtime.ClientEmbeddings.UnembedAll();
            _runtime = null;
        }
        base.OnClosed(e);
    }
}
