using System.Windows;
using System.Windows.Controls;
using VpnUs.App.ViewModels;

namespace VpnUs.App.Views;

public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();

    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppsViewModel viewModel && viewModel.MarkDirtyCommand.CanExecute(null))
        {
            viewModel.MarkDirtyCommand.Execute(null);
        }
    }
}
