using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ChatRelay.Host;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ChatRelay.Chat.Views;

public partial class McpToolMenu : UserControl
{
    readonly HostClient _host;
    bool _refreshing;

    public McpToolMenu(HostClient host)
    {
        _host = host;
        InitializeComponent();
        _host.McpServerChanged += server => UiThread.OnUi(() => { _ = LoadAsync(); });
    }

    public async Task LoadAsync()
    {
        if (_host is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var servers = await _host.ListMcpServersAsync();
            var settings = await _host.GetSettingsAsync();
            var disabledServers = new HashSet<string>(settings.Permissions.DisabledMcpServers, StringComparer.Ordinal);
            var disabledTools = new HashSet<string>(settings.Permissions.DisabledMcpTools, StringComparer.Ordinal);

            await UiThread.SwitchToUi();
            ServersPanel.Children.Clear();
            if (servers.Count == 0)
            {
                ServersPanel.Children.Add(Message("No MCP servers configured. Add one from Settings → MCP."));
                StatusText.Text = "0 servers";
                return;
            }
            foreach (var s in servers) ServersPanel.Children.Add(BuildServerRow(s, disabledServers, disabledTools));
            StatusText.Text = servers.Count + " server(s)";
        }
        finally { _refreshing = false; }
    }

    FrameworkElement BuildServerRow(McpServerSummary s, HashSet<string> disabledServers, HashSet<string> disabledTools)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = TextLabel($"{s.Name}  ({DescribeStatus(s)})", semi: true);
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        var serverDisabled = disabledServers.Contains(s.Id);
        var serverBox = new CheckBox
        {
            IsThreeState = false,
            IsChecked = !serverDisabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0),
        };
        serverBox.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Grid.SetColumn(serverBox, 1);
        header.Children.Add(serverBox);

        var body = new StackPanel { Margin = new Thickness(18, 2, 6, 4) };
        var toolBoxes = new List<CheckBox>();

        if (s.Tools.Count == 0)
        {
            body.Children.Add(Message(s.Status == "error"
                ? "Error: " + (s.ErrorMessage ?? "unknown")
                : s.Status == "running" ? "Server reported no tools." : "Connecting…"));
        }
        else
        {
            foreach (var t in s.Tools)
            {
                var toolId = $"mcp__{s.Id}__{t.Name}";
                var toolDisabled = serverDisabled || disabledTools.Contains(toolId);
                var box = new CheckBox
                {
                    Content = t.Name,
                    IsChecked = !toolDisabled,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 2),
                    ToolTip = t.Description,
                    Tag = (ServerId: s.Id, Tool: t.Name),
                };
                box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                toolBoxes.Add(box);
                body.Children.Add(box);
                box.Click += async (_, _) => await OnToolToggledAsync(box, serverBox, toolBoxes);
            }
        }

        SyncServerBoxState(serverBox, toolBoxes, serverDisabled);
        serverBox.Click += async (_, _) => await OnServerToggledAsync(s.Id, serverBox, toolBoxes);

        var expander = new Expander
        {
            Header = header,
            IsExpanded = false,
            Margin = new Thickness(8, 2, 8, 2),
            Content = body,
        };
        expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return expander;
    }

    async Task OnServerToggledAsync(string serverId, CheckBox serverBox, List<CheckBox> toolBoxes)
    {
        if (serverBox.IsChecked is null) return;
        var enabled = serverBox.IsChecked == true;
        foreach (var b in toolBoxes) b.IsChecked = enabled;
        try { await _host.SetMcpServerEnabledAsync(serverId, enabled); }
        catch (Exception ex) { await UiThread.SwitchToUi(); StatusText.Text = "Error: " + ex.Message; }
    }

    async Task OnToolToggledAsync(CheckBox toolBox, CheckBox serverBox, List<CheckBox> toolBoxes)
    {
        var tag = ((string ServerId, string Tool))toolBox.Tag;
        SyncServerBoxState(serverBox, toolBoxes, serverExplicitlyDisabled: false);
        try { await _host.SetMcpToolEnabledAsync(tag.ServerId, tag.Tool, toolBox.IsChecked == true); }
        catch (Exception ex) { await UiThread.SwitchToUi(); StatusText.Text = "Error: " + ex.Message; }
    }

    static void SyncServerBoxState(CheckBox serverBox, List<CheckBox> toolBoxes, bool serverExplicitlyDisabled)
    {
        if (serverExplicitlyDisabled) { serverBox.IsChecked = false; return; }
        if (toolBoxes.Count == 0) { serverBox.IsChecked = true; return; }
        var on = toolBoxes.Count(b => b.IsChecked == true);
        serverBox.IsChecked = on == toolBoxes.Count ? true : on == 0 ? false : (bool?)null;
    }

    async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    static string DescribeStatus(McpServerSummary s) => s.Status switch
    {
        "running" => $"{s.Tools.Count} tools",
        "starting" => "starting",
        "error" => "error",
        _ => "stopped",
    };

    static TextBlock TextLabel(string text, bool semi = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return tb;
    }

    static TextBlock Message(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 4, 10, 8),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return tb;
    }

}
