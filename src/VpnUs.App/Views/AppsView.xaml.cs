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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            DataContext is AppsViewModel vm)
        {
            vm.AddDroppedFiles(files);
            e.Handled = true;
        }
    }
}
