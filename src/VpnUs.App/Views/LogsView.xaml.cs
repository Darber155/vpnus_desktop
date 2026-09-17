using System.Collections.Specialized;
using System.Windows.Controls;
using VpnUs.App.ViewModels;

namespace VpnUs.App.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LogsViewModel oldViewModel)
        {
            oldViewModel.Lines.CollectionChanged -= OnLinesChanged;
        }

        if (e.NewValue is LogsViewModel newViewModel)
        {
            newViewModel.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not LogsViewModel { AutoScroll: true } || LogList.Items.Count == 0)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 } items)
        {
            LogList.ScrollIntoView(items[items.Count - 1]);
        }
    }
}
