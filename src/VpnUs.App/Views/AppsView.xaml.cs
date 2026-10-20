using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VpnUs.App.ViewModels;

namespace VpnUs.App.Views;

public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<Button>(source) is not null ||
            FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<TextBox>(source) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: AppRowViewModel row })
        {
            row.IsSelected = !row.IsSelected;
        }
    }

    private void OnSelectionMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AppRowViewModel row } && DataContext is AppsViewModel viewModel)
        {
            viewModel.OpenFileLocationCommand.Execute(row);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AppRowViewModel row } && DataContext is AppsViewModel viewModel)
        {
            viewModel.CopyPathCommand.Execute(row);
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

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
