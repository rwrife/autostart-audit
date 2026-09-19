using System.Xml.Linq;

namespace AutostartAudit.App.Tests;

/// <summary>
/// Structural assertions over the WPF XAML. These parse the markup (no WPF
/// runtime needed) so every CI host can enforce the accessibility contract;
/// the live rendering is additionally smoke-tested on windows-latest.
/// </summary>
public class MainWindowXamlTests
{
    private static readonly XNamespace P =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace X =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement LoadWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestFiles", "MainWindow.xaml");
        Assert.True(File.Exists(path), $"missing test copy of MainWindow.xaml at {path}");
        return XElement.Load(path);
    }

    [Fact]
    public void StatusColumnsArePlainText_NotColorOnly()
    {
        var window = LoadWindow();
        // Status is conveyed by text columns (screen-reader readable bindings).
        var statusColumns = window.Descendants()
            .Where(e => e.Name == P + "DataGridTextColumn")
            .Where(e => ((string?)e.Attribute("Header")) is "Signing" or "Health" or "Result" or "Restore" or "Quarantine")
            .ToList();
        Assert.NotEmpty(statusColumns);
        Assert.All(statusColumns, c => Assert.NotNull(c.Attribute("Binding")));
        // Color-only conveyance would need template columns with shapes; the
        // window deliberately has none.
        var elements = window.Descendants().ToList();
        Assert.DoesNotContain(elements, e => e.Name == P + "DataGridTemplateColumn");
        Assert.DoesNotContain(elements, e => e.Name == P + "Rectangle" || e.Name == P + "Ellipse");
    }

    [Fact]
    public void AllInteractiveControlsCarryAutomationNames()
    {
        var window = LoadWindow();
        var interactive = window.Descendants().Where(e =>
            e.Name == P + "Button"
            || e.Name == P + "TextBox"
            || e.Name == P + "ComboBox"
            || e.Name == P + "DataGrid"
            || e.Name == P + "TabItem"
            || e.Name == P + "ToolBar"
            || e.Name == P + "StatusBar").ToList();
        Assert.NotEmpty(interactive);
        foreach (var element in interactive)
        {
            // Attached properties are flat dotted attribute names in XAML.
            var name = element.Attribute("AutomationProperties.Name");
            Assert.True(!string.IsNullOrWhiteSpace(name?.Value),
                $"{element.Name.LocalName} (x:Name={element.Attribute(X + "Name")?.Value ?? "?"}) lacks AutomationProperties.Name");
        }
    }

    [Fact]
    public void ColumnsCarryAutomationNames()
    {
        var window = LoadWindow();
        var columns = window.Descendants()
            .Where(e => e.Name == P + "DataGridTextColumn").ToList();
        Assert.NotEmpty(columns);
        foreach (var column in columns)
            Assert.True(!string.IsNullOrWhiteSpace(column.Attribute("AutomationProperties.Name")?.Value),
                $"column '{column.Attribute("Header")?.Value}' lacks AutomationProperties.Name");
    }

    [Fact]
    public void InventoryGridIsVirtualized()
    {
        var window = LoadWindow();
        var grid = window.Descendants().Single(e => e.Name == P + "DataGrid"
            && (string?)e.Attribute(X + "Name") == "InventoryGrid");
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizing"));
    }

    [Fact]
    public void KeyboardShortcutsAreBound()
    {
        var window = LoadWindow();
        var bindings = window.Descendants()
            .Where(e => e.Name == P + "KeyBinding")
            .Select(e => (Key: (string?)e.Attribute("Key"), Cmd: (string?)e.Attribute("Command")))
            .ToList();
        Assert.Contains(bindings, b => b.Key == "F5" && b.Cmd!.Contains("ScanCommand", StringComparison.Ordinal));
        Assert.Contains(bindings, b => b.Cmd!.Contains("ExportCommand", StringComparison.Ordinal));
        Assert.Contains(bindings, b => b.Cmd!.Contains("CaptureSnapshotCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletenessFooterIsPersistent_AcrossAllTabs()
    {
        var window = LoadWindow();
        // The completeness statement binds ScanCompleteText and must NOT live
        // inside the TabControl — it stays visible on every tab.
        var scanText = window.Descendants().Single(e =>
            e.Name == P + "TextBlock"
            && ((string?)e.Attribute("Text"))?.Contains("ScanCompleteText", StringComparison.Ordinal) == true);
        for (var p = scanText.Parent; p is not null; p = p.Parent)
            Assert.NotEqual(P + "TabControl", p.Name);
    }
}
