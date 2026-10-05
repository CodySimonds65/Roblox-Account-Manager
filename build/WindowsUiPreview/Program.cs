using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

// Offscreen visual QA of source XAML and compiled dialogs with fixture data. No App startup, accounts,
// browser sessions, native hosts, network operations or user settings are loaded.
internal static class Program
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "Click", "Checked", "Unchecked", "Loaded", "SelectionChanged", "TextChanged",
        "PreviewMouseLeftButtonDown", "PreviewMouseMove", "Drop", "LayoutUpdated", "SizeChanged"
    };

    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var output = Path.Combine(root, "artifacts", "windows-ui-refresh", "previews");
        Directory.CreateDirectory(output);
        var app = new Application();
        var source = XDocument.Load(Path.Combine(root, "client", "App.xaml"));
        var dictionary = new XElement(Wpf + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", Xaml),
            source.Root!.Element(Wpf + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());

        foreach (var (file, width, height) in new[]
        {
            ("MainWindow", 1380, 860), ("MainWindow", 1080, 700),
            ("SettingsDialog", 1080, 800), ("SettingsDialog", 900, 650),
            ("PluginsWindow", 900, 680), ("PluginsWindow", 720, 560),
            ("CompatibilityDialog", 760, 590), ("CompatibilityDialog", 640, 480),
            ("InputDialog", 440, 300), ("GamePresetDialog", 480, 340),
            ("PluginConsentWindow", 560, 430)
        })
        {
            var document = XDocument.Load(Path.Combine(root, "client", file + ".xaml"));
            Clean(document.Root!, root);
            Window window = file switch
            {
                "SettingsDialog" => new RobloxAltClient.SettingsDialog(new RobloxAccountManager.Core.Models.LauncherSettings(), [], []),
                "CompatibilityDialog" => new RobloxAltClient.CompatibilityDialog([]),
                _ => (Window)XamlReader.Parse(document.ToString())
            };
            Console.WriteLine($"{file}: font={window.FontFamily}, size={window.FontSize}, style={window.Style?.TargetType.Name ?? "none"}");
            if (window.Style is null || window.FontSize != 13 || !window.UseLayoutRounding)
                throw new InvalidOperationException($"{file} did not apply shared window typography and layout rounding.");
            Fixture(window, file);
            var body = (FrameworkElement)window.Content;
            // Window chrome is excluded; reserve its height in layout measurements.
            var size = new Size(width - 2, height - 34);
            body.Measure(size);
            body.Arrange(new Rect(size));
            body.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(body), null,
                    new Rect(body.Margin.Left, body.Margin.Top, body.ActualWidth, body.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(output, $"{file}-{width}x{height}.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine(path);
            if (file == "MainWindow")
            {
                var launch = (Button)window.FindName("AutoLaunchButton");
                var game = (ComboBox)window.FindName("GamePicker");
                var workspace = (Grid)window.FindName("WorkspaceGrid");
                if (launch.ActualWidth < launch.DesiredSize.Width - 1 || game.ActualWidth < 160)
                    throw new InvalidOperationException("Launch controls are clipped.");
                Console.WriteLine($"Workspace {workspace.ActualWidth:0}x{workspace.ActualHeight:0}; browser row {workspace.RowDefinitions[2].ActualHeight:0} DIP");
            }
        }
    }

    private static void Clean(XElement element, string root)
    {
        foreach (var attribute in element.Attributes().ToArray())
            if (attribute.Name == Xaml + "Class" || Events.Contains(attribute.Name.LocalName))
                attribute.Remove();
        foreach (var child in element.Elements().ToArray())
        {
            if (child.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                if (child.Name.LocalName == "GameSettingsEditor")
                {
                    var editor = XDocument.Load(Path.Combine(root, "client", "GameSettingsEditor.xaml")).Root!;
                    Clean(editor, root);
                    // Inlining removes the compiled UserControl's namescope.
                    // Prefix its internal names so three editor instances coexist.
                    var prefix = child.Attribute(Xaml + "Name")!.Value;
                    foreach (var attribute in editor.DescendantsAndSelf().Attributes().ToArray())
                    {
                        if (attribute.Name == Xaml + "Name") attribute.Value = prefix + attribute.Value;
                        else if (attribute.Value.Contains("ElementName=", StringComparison.Ordinal))
                            attribute.Value = attribute.Value.Replace("ElementName=", "ElementName=" + prefix, StringComparison.Ordinal);
                    }
                    foreach (var attribute in child.Attributes().Where(a => a.Name.LocalName is "Name" or "Grid.Row"))
                        editor.SetAttributeValue(attribute.Name, attribute.Value);
                    child.ReplaceWith(editor);
                }
                else child.ReplaceWith(new XElement(Wpf + "Border",
                    child.Attributes().Where(a => a.Name.LocalName is "Name" or "Grid.Row" or "Visibility")));
            }
            else Clean(child, root);
        }
    }

    private static void Fixture(Window window, string file)
    {
        if (file == "MainWindow")
        {
            var accounts = (ListBox)window.FindName("AccountsList");
            accounts.ItemsSource = new[]
            {
                new AccountSample("Dweebyy", "", true),
                new AccountSample("Builder", "Testing", false),
                new AccountSample("Weekend account", "Testing", false)
            };
            accounts.SelectedIndex = 0;
            var games = (ComboBox)window.FindName("GamePicker");
            games.Items.Add("Dungeon Quest Reborn");
            games.Items.Add("Custom URL");
            games.SelectedIndex = 0;
            ((TextBlock)window.FindName("ActiveProfileText")).Text = "Dweebyy";
            ((TextBlock)window.FindName("PresetHintText")).Text = "Ready to launch Dungeon Quest Reborn";
            ((TextBox)window.FindName("ActivityLog")).Text = "14:32:08  Ready. Select accounts and a game.\n14:32:08  Roblox Account Manager is up to date.";
        }
        if (file == "SettingsDialog")
        {
            foreach (var name in new[] { "UpdateChecksBox", "ContinueOnFailureBox", "RememberSelectionsBox" })
                ((CheckBox)window.FindName(name)).IsChecked = true;
            foreach (var name in new[] { "TimeoutBox", "DelayBox", "LauncherBox" })
                ((ComboBox)window.FindName(name)).SelectedIndex = 0;
        }
        if (file == "CompatibilityDialog")
            ((ListBox)window.FindName("ChecksList")).ItemsSource = new[]
            {
                new CheckSample("Client", "Ready", "Version 2.10.0", "Automatic update checks enabled"),
                new CheckSample("Windows", "Info", "Windows 11", "64-bit operating system"),
                new CheckSample("WebView2", "Ready", "Installed", "Runtime 152.0.4191.66"),
                new CheckSample("Roblox launcher", "Ready", "Standard Roblox detected", "RobloxPlayerBeta is installed"),
                new CheckSample("Sysinternals Handle", "Info", "Downloads on first use", "Retrieved directly from Microsoft"),
                new CheckSample("Permissions", "Warning", "Some clients may need attention", "Review the launcher and game process permissions.")
            };
        if (file == "PluginsWindow")
        {
            var available = (ItemsControl)window.FindName("AvailableList");
            foreach (var (name, description) in new[]
            {
                ("RAM Macros", "Foreground macro recording and playback; focus may switch briefly."),
                ("RAM OCR", "Foreground window-relative OCR and color triggers."),
                ("RAM AFK", "Staggered foreground keep-alive for enabled accounts.")
            })
            {
                // Fixture rows match the code-built catalog rows. This validates
                // their layout, not plugin install/launch behavior.
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel();
                details.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
                details.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)window.FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 3, 16, 0) });
                grid.Children.Add(details);
                var install = new Button { Content = "Install", Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(install, 1);
                grid.Children.Add(install);
                available.Items.Add(new Border { Child = grid, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 4),
                    Background = (Brush)window.FindResource("SurfaceBrush"), BorderBrush = (Brush)window.FindResource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
            }
        }
    }
}

public sealed record AccountSample(string Label, string Group, bool IsFavorite);
public sealed record CheckSample(string Name, string State, string Summary, string Detail)
{
    public string StateLabel => State.ToUpperInvariant();
}
