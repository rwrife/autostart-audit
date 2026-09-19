using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using AutostartAudit.App.Model;
using AutostartAudit.Core.Export;
using Microsoft.Win32;

namespace AutostartAudit.App;

public partial class MainWindow : System.Windows.Window
{
    public MainViewModel ViewModel { get; }
    private readonly ICollectionView? _inventoryView;

    /// <summary>XAML designer / default ctor: wires the live machine service.</summary>
    public MainWindow() : this(LiveWorkspaceService.ForCurrentOS())
    {
    }

    /// <summary>Test/smoke ctor: any fixture-backed service, zero real-machine access.</summary>
    public MainWindow(IScanService service)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(service);
        DataContext = ViewModel;

        // Per-row visibility filtering keeps the ObservableCollection stable
        // (virtualization-friendly); the view refreshes on FilterVersion.
        _inventoryView = CollectionViewSource.GetDefaultView(ViewModel.Entries);
        _inventoryView.Filter = o => o is EntryRow row && row.Visible;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FilterVersion))
                _inventoryView.Refresh();
        };

        // The save dialog lives in the view; the VM asks for a path.
        ViewModel.ExportPathRequested = format => Task.FromResult<string?>(
            Dispatcher.Invoke(() =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export report",
                    Filter = format == ExportFormat.Json
                        ? "JSON report (*.json)|*.json"
                        : "Markdown report (*.md)|*.md",
                    FileName = format == ExportFormat.Json ? "autostart-report.json" : "autostart-report.md",
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            }));

        Loaded += async (_, _) => await ViewModel.ScanAsync();
    }
}
