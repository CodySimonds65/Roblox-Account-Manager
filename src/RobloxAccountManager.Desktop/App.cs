using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Desktop.Views;
using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public sealed class App : Application
{
    private static DesktopValidationMode _configuredValidationMode;
    private static string? _configuredDataRoot;
    private readonly DesktopValidationMode _validationMode;
    private readonly string? _dataRoot;

    public App()
    {
        _validationMode = _configuredValidationMode;
        _dataRoot = _configuredDataRoot;
    }

    internal static void ConfigureStartup(DesktopValidationMode validationMode, string? dataRoot)
    {
        _configuredValidationMode = validationMode;
        _configuredDataRoot = dataRoot;
    }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))),
                new Setter(Button.ForegroundProperty, Brushes.White),
                new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(9)),
                new Setter(Button.PaddingProperty, new Thickness(15, 9)),
                new Setter(Button.FontSizeProperty, 13d),
                new Setter(Button.FontWeightProperty, FontWeight.SemiBold),
                new Setter(TemplatedControl.TemplateProperty, CreateButtonTemplate())
            }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class(":disabled"))
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.48d) }
        });
        Styles.Add(new Style(selector => selector.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.Parse("#0D1016"))),
                new Setter(TextBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#272D3A"))),
                new Setter(TextBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(TextBox.PaddingProperty, new Thickness(11, 8)),
                new Setter(TextBox.FontSizeProperty, 13d),
                new Setter(TemplatedControl.TemplateProperty, CreateTextBoxTemplate())
            }
        });
        Styles.Add(new Style(selector => selector.OfType<TextBox>().Class(":focus"))
        {
            Setters = { new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<TextBox>().Class(":disabled"))
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.5d) }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(ComboBox.BackgroundProperty, new SolidColorBrush(Color.Parse("#0D1016"))),
                new Setter(ComboBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(ComboBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#272D3A"))),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(ComboBox.PaddingProperty, new Thickness(11, 8)),
                new Setter(ComboBox.FontSizeProperty, 13d),
                new Setter(ComboBox.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBox>().Class(":pointerover"))
        {
            Setters = { new Setter(ComboBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#41495E"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>())
        {
            Setters =
            {
                new Setter(ComboBoxItem.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(ComboBoxItem.BackgroundProperty, Brushes.Transparent),
                new Setter(ComboBoxItem.PaddingProperty, new Thickness(11, 8)),
                new Setter(ComboBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>().Class(":pointerover"))
        {
            Setters = { new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.Parse("#1D2230"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.Parse("#2A234D"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<ListBox>())
        {
            Setters =
            {
                new Setter(ListBox.BackgroundProperty, Brushes.Transparent),
                new Setter(ListBox.BorderThicknessProperty, new Thickness(0)),
                new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent),
                new Setter(ListBoxItem.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MarginProperty, new Thickness(0, 0, 0, 7)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(TemplatedControl.TemplateProperty, CreateListBoxItemTemplate())
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":pointerover"))
        {
            Setters = { new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.Parse("#1D2230"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters =
            {
                new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.Parse("#2A234D"))),
                new Setter(ListBoxItem.BorderBrushProperty, new SolidColorBrush(Color.Parse("#544394")))
            }
        });
        Styles.Add(new Style(selector => selector.OfType<CheckBox>())
        {
            Setters =
            {
                new Setter(CheckBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(TemplatedControl.TemplateProperty, CreateCheckBoxTemplate())
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ScrollBar>())
        {
            Setters =
            {
                new Setter(ScrollBar.WidthProperty, 12d),
                new Setter(ScrollBar.BackgroundProperty, Brushes.Transparent)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<Thumb>())
        {
            Setters =
            {
                new Setter(Thumb.BackgroundProperty, new SolidColorBrush(Color.Parse("#4A5266"))),
                new Setter(Thumb.CornerRadiusProperty, new CornerRadius(5)),
                new Setter(Thumb.MinHeightProperty, 28d),
                new Setter(Thumb.MinWidthProperty, 28d)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<Thumb>().Class(":pointerover"))
        {
            Setters = { new Setter(Thumb.BackgroundProperty, new SolidColorBrush(Color.Parse("#626C84"))) }
        });
        Styles.Add(new Style(selector => selector.OfType<Thumb>().Class(":pressed"))
        {
            Setters = { new Setter(Thumb.BackgroundProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))) }
        });
    }

    private static IControlTemplate CreateButtonTemplate() => new FuncControlTemplate<Button>((button, scope) =>
    {
        var border = new Border { CornerRadius = new CornerRadius(9) };
        border.Bind(Border.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty));
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty));
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(Button.BorderThicknessProperty));
        border.Bind(Border.PaddingProperty, new TemplateBinding(Button.PaddingProperty));
        var content = new ContentPresenter { VerticalContentAlignment = VerticalAlignment.Center };
        content.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        content.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentControl.ContentTemplateProperty));
        content.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty));
        content.Bind(ContentPresenter.VerticalContentAlignmentProperty, new TemplateBinding(ContentControl.VerticalContentAlignmentProperty));
        border.Child = content;
        return border;
    });

    private static IControlTemplate CreateTextBoxTemplate() => new FuncControlTemplate<TextBox>((textBox, scope) =>
    {
        var border = new Border { CornerRadius = new CornerRadius(8) };
        border.Bind(Border.BackgroundProperty, new TemplateBinding(TextBox.BackgroundProperty));
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(TextBox.BorderBrushProperty));
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(TextBox.BorderThicknessProperty));
        border.Bind(Border.PaddingProperty, new TemplateBinding(TextBox.PaddingProperty));
        var presenter = new TextPresenter { Name = "PART_TextPresenter" };
        scope.Register("PART_TextPresenter", presenter);
        presenter.Bind(TextPresenter.TextProperty, new TemplateBinding(TextBox.TextProperty));
        presenter.Foreground = textBox.Foreground;
        presenter.FontFamily = textBox.FontFamily;
        presenter.FontSize = textBox.FontSize;
        presenter.FontWeight = textBox.FontWeight;
        presenter.Bind(TextPresenter.TextWrappingProperty, new TemplateBinding(TextBox.TextWrappingProperty));
        presenter.Bind(TextPresenter.TextAlignmentProperty, new TemplateBinding(TextBox.TextAlignmentProperty));
        presenter.Bind(TextPresenter.SelectionBrushProperty, new TemplateBinding(TextBox.SelectionBrushProperty));
        presenter.Bind(TextPresenter.SelectionForegroundBrushProperty, new TemplateBinding(TextBox.SelectionForegroundBrushProperty));
        presenter.Bind(TextPresenter.CaretBrushProperty, new TemplateBinding(TextBox.CaretBrushProperty));
        presenter.Bind(TextPresenter.CaretIndexProperty, new TemplateBinding(TextBox.CaretIndexProperty));
        presenter.Bind(TextPresenter.SelectionStartProperty, new TemplateBinding(TextBox.SelectionStartProperty));
        presenter.Bind(TextPresenter.SelectionEndProperty, new TemplateBinding(TextBox.SelectionEndProperty));
        var scroll = new ScrollViewer
        {
            Name = "PART_ScrollViewer",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = presenter
        };
        scope.Register("PART_ScrollViewer", scroll);
        border.Child = scroll;
        return border;
    });

    private static IControlTemplate CreateListBoxItemTemplate() => new FuncControlTemplate<ListBoxItem>((item, scope) =>
    {
        var border = new Border { CornerRadius = new CornerRadius(10) };
        border.Bind(Border.BackgroundProperty, new TemplateBinding(ListBoxItem.BackgroundProperty));
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(ListBoxItem.BorderBrushProperty));
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(ListBoxItem.BorderThicknessProperty));
        var content = new ContentPresenter();
        content.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        content.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentControl.ContentTemplateProperty));
        content.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty));
        content.Bind(ContentPresenter.VerticalContentAlignmentProperty, new TemplateBinding(ContentControl.VerticalContentAlignmentProperty));
        border.Child = content;
        return border;
    });

    private static IControlTemplate CreateCheckBoxTemplate() => new FuncControlTemplate<CheckBox>((checkBox, scope) =>
    {
        var grid = new Grid
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(12, 10),
            ColumnDefinitions = new ColumnDefinitions("34,*")
        };
        var surface = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#0D1016")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A4254")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        var mark = new TextBlock
        {
            Text = "✓",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        mark.Bind(TextBlock.IsVisibleProperty, new Binding("IsChecked") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        surface.Child = mark;
        surface.Bind(Border.BackgroundProperty, new Binding("IsChecked")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Converter = new CheckedBrushConverter(new SolidColorBrush(Color.Parse("#7C5CFC")), new SolidColorBrush(Color.Parse("#0D1016")))
        });
        surface.Bind(Border.BorderBrushProperty, new Binding("IsChecked")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Converter = new CheckedBrushConverter(new SolidColorBrush(Color.Parse("#7C5CFC")), new SolidColorBrush(Color.Parse("#3A4254")))
        });
        grid.Children.Add(surface);
        var content = new ContentPresenter { VerticalContentAlignment = VerticalAlignment.Center };
        content.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        content.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentControl.ContentTemplateProperty));
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    });

    private sealed class CheckedBrushConverter(IBrush checkedBrush, IBrush uncheckedBrush) : Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? checkedBrush : uncheckedBrush;
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => AvaloniaProperty.UnsetValue;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = OperatingSystem.IsMacOS() ? RobloxPlatform.MacOS :
                OperatingSystem.IsWindows() ? RobloxPlatform.Windows : RobloxPlatform.Unknown;
            var composition = DesktopComposition.Create(
                platform,
                TrustedRobloxIdentityConfiguration.LoadInstallerIdentity(),
                _dataRoot);
            var shell = new DesktopShellViewModel(composition.Capabilities, composition.Accounts, composition.Presets, composition.Settings, composition.Updates, composition.UpdateSource, composition.RobloxSettings, composition.Plugins);
            if (composition.Plugins is MacPluginHostFacade macPlugins)
            {
                macPlugins.SetAccountSnapshotProvider(() => shell.Accounts.Select(account => new PluginAccountSnapshot(account.Id, account.Label, RobloxPlatform.MacOS)).ToArray());
            }
            desktop.MainWindow = new MainWindow(
                shell,
                composition.BrowserSessions,
                composition.Launches,
                composition.Clients,
                composition.UpdateSource,
                _validationMode);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
