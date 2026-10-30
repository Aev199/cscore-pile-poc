using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SectionApp.ViewModels;

namespace SectionApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Storage = () => StorageProvider;
            vm.Profile.Storage = () => StorageProvider;
            vm.ClipboardText = async () => Clipboard is { } c ? await c.TryGetTextAsync() : null;
            // γf and kдл per row only when the rows differ or on demand: the columns are not in the visual tree.
            void Columns()
            {
                foreach (var column in LoadsGrid.Columns.Where(c => c.Header is "γf" or "kдл")) column.IsVisible = vm.ShowFactorColumns;
            }
            Columns();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ShowFactorColumns)) Columns(); };
        };
        // Outside cell editing: Ctrl+V pastes rows from Excel, Delete removes the selected row.
        LoadsGrid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not MainViewModel vm || e.Source is TextBox) return;
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { vm.PasteLoadsCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Delete) { vm.RemoveLoadCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Bubble);
    }
}
