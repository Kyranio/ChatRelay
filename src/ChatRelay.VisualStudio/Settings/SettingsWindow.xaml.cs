using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ChatRelay.Host;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace ChatRelay.Settings;

public partial class SettingsWindow : Window
{
    readonly HostClient _host;
    ExtensionSettings? _current;

    public SettingsWindow(HostClient host)
    {
        _host = host;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            _current = await _host.GetSettingsAsync();
            await UiThread.SwitchToUi();
            PopulateGeneral();
            PopulatePermissions();
            PopulateMcpFiles();
            await RefreshMcpServersAsync();
            CategoryList.SelectedIndex = 0;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void PopulateGeneral()
    {
        AutoAttachCheck.IsChecked = _current!.General.AutoAttachActiveFile;
        ThinkingExpandedCheck.IsChecked = _current.General.ThinkingExpandedByDefault;
    }

    void PopulatePermissions()
    {
        AllowedToolsBox.Text = Join(_current!.Permissions.AllowedTools);
        DisallowedToolsBox.Text = Join(_current.Permissions.DisallowedTools);
        AdditionalDirsBox.Text = Join(_current.Permissions.AdditionalDirectories);
    }

    void PopulateMcpFiles()
    {
        McpFileList.Items.Clear();
        foreach (var f in _current!.McpFiles)
            McpFileList.Items.Add(BuildFileRow(f));
    }

    async Task RefreshMcpServersAsync()
    {
        var servers = await _host.ListMcpServersAsync();
        await UiThread.SwitchToUi();
        McpServerList.Items.Clear();
        foreach (var s in servers)
            McpServerList.Items.Add(BuildServerRow(s));
        McpEmptyState.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        McpServerListScroll.Visibility = servers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    FrameworkElement BuildServerRow(McpServerSummary s)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Controls — vary by status, mirroring the chat MCP popup.
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        switch (s.Status)
        {
            case "running":
                controls.Children.Add(IconButton("↻", "Restart server", async () => { await _host.RestartMcpServerAsync(s.Id); await RefreshMcpServersAsync(); }));
                controls.Children.Add(IconButton("■", "Stop server", async () => { await _host.StopMcpServerAsync(s.Id); await RefreshMcpServersAsync(); }, accent: true));
                break;
            case "starting":
                controls.Children.Add(DisabledIcon("…", "Starting…"));
                break;
            default:
                controls.Children.Add(IconButton("▶", "Start server", async () => { await _host.StartMcpServerAsync(s.Id); await RefreshMcpServersAsync(); }, accent: true));
                break;
        }
        Grid.SetColumn(controls, 0);
        grid.Children.Add(controls);

        // Name + status detail.
        var info = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(ThemedText(s.Name, semi: true));
        var detailText = s.Status switch
        {
            "running" => $"{s.Tools.Count} tools  •  {s.Source}",
            "starting" => $"starting…  •  {s.Source}",
            "error"   => $"error  •  {s.Source}",
            _         => $"stopped  •  {s.Source}",
        };
        var detail = ThemedText(detailText, size: 10);
        detail.Opacity = 0.65;
        info.Children.Add(detail);
        if (s.Status == "error" && !string.IsNullOrEmpty(s.ErrorMessage))
        {
            var err = ThemedText(s.ErrorMessage!, size: 10);
            err.Foreground = Brushes.IndianRed;
            err.TextWrapping = TextWrapping.Wrap;
            err.Margin = new Thickness(0, 1, 0, 0);
            info.Children.Add(err);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        return new Border
        {
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(2),
            Child = grid,
        };
    }

    Button DisabledIcon(string glyph, string tooltip)
    {
        var b = IconButton(glyph, tooltip, () => Task.CompletedTask);
        b.IsEnabled = false;
        b.Opacity = 0.6;
        return b;
    }

    FrameworkElement BuildFileRow(TrackedMcpFile f)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pathBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = f.FilePath,
        };
        pathBlock.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        var link = new Hyperlink(new Run(ShortenPath(f.FilePath))) { TextDecorations = TextDecorations.Underline };
        link.Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0xD0));
        link.Click += (_, _) => OpenInEditor(f.FilePath);
        pathBlock.Inlines.Add(link);
        Grid.SetRow(pathBlock, 0); Grid.SetColumn(pathBlock, 0);
        grid.Children.Add(pathBlock);

        var trash = new Button
        {
            Content = "🗑",
            FontSize = 12,
            Width = 26, Height = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(0),
            ToolTip = "Stop tracking this file",
        };
        trash.SetResourceReference(Button.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        trash.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, $"Stop tracking this configuration file?\n\n{f.FilePath}\n\nThe file on disk will not be deleted.",
                "Remove configuration file", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { await _host.RemoveMcpFileAsync(f.FilePath); _current = await _host.GetSettingsAsync(); await UiThread.SwitchToUi(); PopulateMcpFiles(); }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        Grid.SetRow(trash, 0); Grid.SetColumn(trash, 1);
        grid.Children.Add(trash);

        if (f.Scope == McpFileScope.Project && !string.IsNullOrEmpty(f.ScopedSolutionPath))
        {
            var scope = ThemedText($"scoped to: {Path.GetFileNameWithoutExtension(f.ScopedSolutionPath)}", size: 10);
            scope.Opacity = 0.65;
            scope.ToolTip = f.ScopedSolutionPath;
            Grid.SetRow(scope, 1); Grid.SetColumn(scope, 0);
            grid.Children.Add(scope);
        }

        return grid;
    }

    Button IconButton(string glyph, string tooltip, Func<Task> onClick, bool accent = false)
    {
        var b = new Button
        {
            Content = glyph,
            FontSize = 13,
            Width = 26, Height = 22,
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };
        if (accent) b.Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0xD0));
        else b.SetResourceReference(Button.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        b.Click += async (_, _) => await onClick();
        return b;
    }

    static TextBlock ThemedText(string text, bool semi = false, int size = 11)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return tb;
    }

    static string ShortenPath(string path, int segments = 3)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= segments ? path : ".../" + string.Join("/", parts.Skip(parts.Length - segments));
    }

    // Category switching ---------------------------------------------------

    void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPanel is null) return; // fires during XAML init before fields are bound
        var idx = CategoryList.SelectedIndex;
        GeneralPanel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        PermissionsPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MCP file buttons -----------------------------------------------------

    void ConfigureGlobalButton_Click(object sender, RoutedEventArgs e) =>
        OpenInEditor(GlobalConfigPath());

    void ConfigureProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ProjectConfigPath();
        if (path is not null) OpenInEditor(path);
        else ShowError("No solution is open — can't resolve a project-scoped config path.");
    }

    void AddMcpFileButton_Click(object sender, RoutedEventArgs e)
    {
        AddMcpFileMenu.PlacementTarget = (UIElement)sender;
        AddMcpFileMenu.IsOpen = true;
    }

    async void AddGlobalMcpFileMenu_Click(object sender, RoutedEventArgs e) =>
        await AddMcpFileAsync(scope: "global");

    async void AddProjectMcpFileMenu_Click(object sender, RoutedEventArgs e) =>
        await AddMcpFileAsync(scope: "project");

    async Task AddMcpFileAsync(string scope)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ChatRelay MCP configuration (*.chatrelay.mcp.json;*.json)|*.chatrelay.mcp.json;*.json|All files (*.*)|*.*",
            Title = $"Pick {scope} .chatrelay.mcp.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await _host.AddMcpFileAsync(dlg.FileName, scope);
            _current = await _host.GetSettingsAsync();
            await UiThread.SwitchToUi();
            PopulateMcpFiles();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // OK / Cancel ----------------------------------------------------------

    void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Close(); return; }

        // ExtensionSettings is a plain class (settable properties), not a
        // record — mutate in place. Host re-saves on receipt.
        _current.General.AutoAttachActiveFile = AutoAttachCheck.IsChecked == true;
        _current.General.ThinkingExpandedByDefault = ThinkingExpandedCheck.IsChecked == true;
        _current.Permissions.AllowedTools = Split(AllowedToolsBox.Text);
        _current.Permissions.DisallowedTools = Split(DisallowedToolsBox.Text);
        _current.Permissions.AdditionalDirectories = Split(AdditionalDirsBox.Text);

        try
        {
            await _host.UpdateSettingsAsync(_current);
            // JsonRpc resumes on the threadpool — must hop back before
            // touching DialogResult / closing the window.
            await UiThread.SwitchToUi();
            DialogResult = true;
        }
        catch (Exception ex) { await UiThread.SwitchToUi(); ShowError(ex.Message); }
    }

    // Helpers --------------------------------------------------------------

    void ShowError(string msg) => MessageBox.Show(this, msg, "ChatRelay Settings", MessageBoxButton.OK, MessageBoxImage.Warning);

    static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items);
    static List<string> Split(string text) => text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToList();

    static string GlobalConfigPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay", ".chatrelay.mcp.json");

    static string? ProjectConfigPath()
    {
        var dir = Editor.EditorSelectionService.GetSolutionDirectory();
        return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir!, ".chatrelay.mcp.json");
    }

    static void OpenInEditor(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            try { System.IO.File.WriteAllText(path, "{\n  \"mcpServers\": {}\n}\n"); } catch { }
        }
        try
        {
            var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
            dte?.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
        }
        catch { }
    }

}
