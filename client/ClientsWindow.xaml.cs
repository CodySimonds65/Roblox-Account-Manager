using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RobloxAltClient.Plugins;

namespace RobloxAltClient;

public partial class ClientsWindow : Window
{
    private const int TabStripHeight = 44;
    private PluginRuntime? _runtime;
    private nint _hostWindow;

    public ClientsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hostWindow = new WindowInteropHelper(this).Handle;
            _runtime = ((App)Application.Current).PluginRuntime;
            _runtime.ClientEmbeddings.SetHostWindow(_hostWindow);
            _runtime.ClientEmbeddings.EmbeddedRootResolver = ResolveEmbeddedRoot;
            _runtime.ClientEmbeddings.EmbeddedActivate = ActivateEmbedded;
            _runtime.Accounts.AccountChanged += Accounts_AccountChanged;
            _runtime.Accounts.AccountExited += Accounts_AccountExited;
            foreach (var account in _runtime.Accounts.Snapshot()) EnsureTab(account);
            Relayout();
        };
    }

    private nint? ResolveEmbeddedRoot(string accountId)
    {
        if (!IsVisible) return null;
        if (ClientTabs.SelectedItem is not ManagedAccountSnapshot selected || selected.AccountId != accountId) return null;
        return _runtime?.ClientEmbeddings.RootFor(accountId);
    }

    private void ActivateEmbedded(string accountId)
    {
        SelectTab(accountId);
        if (!IsVisible) { Show(); Activate(); }
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
        if (existing is not null) ClientTabs.Items.Remove(existing);
        Relayout();
    }

    private void EnsureTab(ManagedAccountSnapshot account)
    {
        if (ClientTabs.Items.OfType<ManagedAccountSnapshot>().Any(item => item.AccountId == account.AccountId)) return;
        ClientTabs.Items.Add(account);
        if (ClientTabs.SelectedItem is null) ClientTabs.SelectedIndex = 0;
        if (_runtime is not null)
        {
            var root = account.RootWindowHandle != nint.Zero ? account.RootWindowHandle : account.WindowHandle;
            if (root != nint.Zero) _runtime.ClientEmbeddings.TryEmbed(account.AccountId, root);
        }
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

    private void Relayout()
    {
        if (_hostWindow == nint.Zero) return;
        _runtime?.ClientEmbeddings.Layout(_hostWindow, TabStripHeight);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_runtime is not null)
        {
            _runtime.Accounts.AccountChanged -= Accounts_AccountChanged;
            _runtime.Accounts.AccountExited -= Accounts_AccountExited;
            _runtime.ClientEmbeddings.EmbeddedRootResolver = null;
            _runtime.ClientEmbeddings.EmbeddedActivate = null;
            _runtime.ClientEmbeddings.UnembedAll();
        }
        base.OnClosed(e);
    }
}
