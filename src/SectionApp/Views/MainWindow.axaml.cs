using Avalonia.Controls;
using SectionApp.ViewModels;

namespace SectionApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.Storage = () => StorageProvider;
        };
    }
}
