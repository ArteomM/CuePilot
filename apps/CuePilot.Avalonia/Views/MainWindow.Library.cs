using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CuePilotDesktop.Models;
using CuePilotDesktop.ViewModels;

namespace CuePilot.Avalonia.Views;

/// <summary>Session library: drag sessions onto topics, card menus, topic rename.</summary>
public partial class MainWindow
{
    private const string SessionDragFormat = "CuePilot.SessionIds";
    private const double DragDistance = 4;

    private Point _dragStartPoint;
    private SessionSummary? _dragCandidate;
    private bool _dragging;

    private void InitializeLibraryUi()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsEditingTopicName) && _viewModel.IsEditingTopicName)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    TopicNameEditTextBox.Focus();
                    TopicNameEditTextBox.SelectAll();
                }, DispatcherPriority.Input);
            }
        };

        // Session cards are created by the list: their pointer events are caught here on the way down.
        SessionsList.AddHandler(PointerPressedEvent, SessionCard_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SessionsList.AddHandler(PointerMovedEvent, SessionCard_OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        SessionsList.AddHandler(Button.ClickEvent, SessionCard_OnClick, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragEnterEvent, TopicRow_OnDragOver);
        AddHandler(DragDrop.DragOverEvent, TopicRow_OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, TopicRow_OnDragLeave);
        AddHandler(DragDrop.DropEvent, TopicRow_OnDrop);

        MoveCheckedButton.Click += MoveCheckedButton_OnClick;
        TopicTitleText.PointerPressed += TopicTitle_OnPointerPressed;
        TopicNameEditTextBox.KeyDown += TopicNameEditTextBox_OnKeyDown;
        TopicNameEditTextBox.LostFocus += (_, _) => _viewModel.CommitTopicRename();
    }

    private static Button? SessionCardOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } button && button.Classes.Contains("sessionCardDrag")
            ? button
            : null;

    private void SessionCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragCandidate = null;
        if (SessionCardOf(e.Source) is { DataContext: SessionSummary session } &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(this);
            _dragCandidate = session;
        }
    }

    private async void SessionCard_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || SessionCardOf(e.Source) is not { } card)
        {
            return;
        }

        Point moved = e.GetPosition(this) - _dragStartPoint;
        if (Math.Abs(moved.X) < DragDistance && Math.Abs(moved.Y) < DragDistance)
        {
            return;
        }

        SessionSummary session = _dragCandidate;
        _dragCandidate = null;
        string[] ids = _viewModel.GetActionSessionIds(session).ToArray();
        var data = new DataObject();
        data.Set(SessionDragFormat, ids);
        _dragging = true;
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropTargets();
            // The drag swallowed the release: the click that follows must not open the session.
            Dispatcher.UIThread.Post(() => _dragging = false, DispatcherPriority.Background);
        }
    }

    private void SessionCard_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_dragging && SessionCardOf(e.Source) != null)
        {
            e.Handled = true;
        }
    }

    private static TopicItemViewModel? TopicOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("topicRow"))?.DataContext as TopicItemViewModel;

    private void TopicRow_OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(SessionDragFormat))
        {
            return;
        }

        TopicItemViewModel? target = TopicOf(e.Source);
        bool accepts = target?.AcceptsDrop == true;
        e.DragEffects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        foreach (TopicItemViewModel item in _viewModel.TopicItems)
        {
            item.IsDropTarget = accepts && ReferenceEquals(item, target);
        }
    }

    private void TopicRow_OnDragLeave(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(SessionDragFormat) && TopicOf(e.Source) is { } target)
        {
            target.IsDropTarget = false;
        }
    }

    private void TopicRow_OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(SessionDragFormat))
        {
            return;
        }

        ClearDropTargets();
        if (TopicOf(e.Source) is not { AcceptsDrop: true } target || e.Data.Get(SessionDragFormat) is not string[] ids)
        {
            return;
        }

        _viewModel.MoveSessionsToTopic(ids, target);
        e.Handled = true;
    }

    private void ClearDropTargets()
    {
        foreach (TopicItemViewModel item in _viewModel.TopicItems)
        {
            item.IsDropTarget = false;
        }
    }

    /// <summary>Right-click on a session card: open, move to a topic, delete.</summary>
    private void SessionCardMenu_OnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not SessionSummary session)
        {
            return;
        }

        IReadOnlyList<string> ids = _viewModel.GetActionSessionIds(session);
        menu.Items.Clear();
        if (ids.Count == 1)
        {
            menu.Items.Add(CreateMenuItem(_viewModel.L["MenuOpenSession"], () => _viewModel.OpenSessionCommand.Execute(session)));
            menu.Items.Add(new Separator());
        }

        AddMoveItems(menu, ids, session.TopicId);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            ids.Count == 1 ? _viewModel.L["MenuDeleteSession"] : _viewModel.DeleteCheckedText,
            () =>
            {
                if (ids.Count == 1)
                {
                    _viewModel.DeleteSessionCommand.Execute(session);
                }
                else
                {
                    _viewModel.DeleteCheckedSessionsCommand.Execute(null);
                }
            }));
    }

    private void MoveCheckedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        List<string> ids = _viewModel.CompletedSessions.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        AddMoveItems(menu, ids, currentTopicId: null);
        menu.Open(MoveCheckedButton);
    }

    /// <summary>One flat item per topic (no submenus), plus «Без темы» and «Новая тема».</summary>
    private void AddMoveItems(ContextMenu menu, IReadOnlyList<string> ids, string? currentTopicId)
    {
        foreach (TopicItemViewModel item in _viewModel.TopicItems.Where(i => i.IsTopic))
        {
            MenuItem menuItem = CreateMenuItem(
                string.Format(_viewModel.L["MenuMoveToTopic"], item.Name),
                () => _viewModel.MoveSessionsToTopic(ids, item));
            menuItem.IsEnabled = item.Topic!.Id != currentTopicId;
            menu.Items.Add(menuItem);
        }

        // currentTopicId is null for a bulk action and empty for a session that has no topic.
        if (currentTopicId != string.Empty)
        {
            TopicItemViewModel? unsorted = _viewModel.TopicItems.FirstOrDefault(i => i.Kind == TopicItemKind.Unsorted);
            menu.Items.Add(CreateMenuItem(_viewModel.L["MenuRemoveFromTopic"], () => _viewModel.MoveSessionsToTopic(ids, unsorted)));
        }

        menu.Items.Add(CreateMenuItem(_viewModel.L["MenuMoveToNewTopic"], () =>
        {
            _viewModel.CreateTopicCommand.Execute(null);
            _viewModel.MoveSessionsToTopic(ids, _viewModel.SelectedTopicItem);
        }));
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void TopicTitle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel.StartRenameTopicCommand.CanExecute(null))
        {
            _viewModel.StartRenameTopicCommand.Execute(null);
        }
    }

    private void TopicNameEditTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.CommitTopicRename();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelTopicRename();
        }
    }
}
