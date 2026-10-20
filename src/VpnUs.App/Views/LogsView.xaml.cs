using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using VpnUs.App.ViewModels;

namespace VpnUs.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _subscribed;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Attach(DataContext as LogsViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Attach(e.NewValue as LogsViewModel);
    }

    private void Attach(LogsViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribed, viewModel))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.Lines.CollectionChanged -= OnLinesChanged;
        }

        _subscribed = viewModel;
        if (_subscribed is not null)
        {
            _subscribed.Lines.CollectionChanged += OnLinesChanged;
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
