using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CuePilotDesktop.Models;
using CuePilotDesktop.ViewModels;

namespace CuePilotDesktop;

/// <summary>Session library: drag sessions onto topics, card menus, topic rename, title bar theme.</summary>
public partial class MainWindow
{
    private const string SessionDragFormat = "CuePilot.SessionIds";
    private const int DwmUseImmersiveDarkMode = 20;

    private Point _dragStartPoint;
    private SessionSummary? _dragCandidate;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void InitializeLibraryUi()
    {
        _viewModel.ThemeApplied += dark => Dispatcher.Invoke(() => ApplyTitleBarTheme(dark));
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsEditingTopicName) && _viewModel.IsEditingTopicName)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    TopicNameEditTextBox.Focus();
                    TopicNameEditTextBox.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }

            if (e.PropertyName == nameof(MainViewModel.SelectedUiLanguage))
            {
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
            }
        };
        SourceInitialized += (_, _) => ApplyTitleBarTheme(!_viewModel.IsLightTheme);
    }

    /// <summary>Dark or light system title bar to match the palette (Windows 10 2004+ / 11; ignored elsewhere).</summary>
    private void ApplyTitleBarTheme(bool dark)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int value = dark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: keep the system title bar.
        }
    }

    private void SessionCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as SessionSummary;
    }

    private void SessionCard_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null || sender is not Button card)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - _dragStartPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        SessionSummary session = _dragCandidate;
        _dragCandidate = null;
        string[] ids = _viewModel.GetActionSessionIds(session).ToArray();
        var data = new DataObject(SessionDragFormat, ids);
        DragDrop.DoDragDrop(card, data, DragDropEffects.Move);
        // The drag swallowed the mouse-up: release the card so it does not stay pressed or open the session.
        card.ReleaseMouseCapture();
        ClearDropTargets();
    }

    private void TopicRow_OnDragEnter(object sender, DragEventArgs e)
    {
        UpdateDropTarget(sender, e, true);
    }

    private void TopicRow_OnDragOver(object sender, DragEventArgs e)
    {
        UpdateDropTarget(sender, e, true);
    }

    private void TopicRow_OnDragLeave(object sender, DragEventArgs e)
    {
        UpdateDropTarget(sender, e, false);
    }

    private void TopicRow_OnDrop(object sender, DragEventArgs e)
    {
        ClearDropTargets();
        if ((sender as FrameworkElement)?.DataContext is not TopicItemViewModel target ||
            !target.AcceptsDrop ||
            e.Data.GetData(SessionDragFormat) is not string[] ids)
        {
            return;
        }

        _viewModel.MoveSessionsToTopic(ids, target);
        e.Handled = true;
    }

    private static void UpdateDropTarget(object sender, DragEventArgs e, bool over)
    {
        var target = (sender as FrameworkElement)?.DataContext as TopicItemViewModel;
        bool accepts = target?.AcceptsDrop == true && e.Data.GetDataPresent(SessionDragFormat);
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (target != null)
        {
            target.IsDropTarget = accepts && over;
        }
    }

    private void ClearDropTargets()
    {
        foreach (TopicItemViewModel item in _viewModel.TopicItems)
        {
            item.IsDropTarget = false;
        }
    }

    /// <summary>Right-click on a session card: open, move to a topic, delete.</summary>
    private void SessionCard_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button card || card.DataContext is not SessionSummary session || card.ContextMenu == null)
        {
            return;
        }

        IReadOnlyList<string> ids = _viewModel.GetActionSessionIds(session);
        ContextMenu menu = card.ContextMenu;
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

    private void MoveCheckedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        List<string> ids = _viewModel.CompletedSessions.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddMoveItems(menu, ids, currentTopicId: null);
        menu.IsOpen = true;
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

    private void TopicTitle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.StartRenameTopicCommand.CanExecute(null))
        {
            _viewModel.StartRenameTopicCommand.Execute(null);
        }
    }

    private void TopicNameEditTextBox_OnKeyDown(object sender, KeyEventArgs e)
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

    private void TopicNameEditTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.CommitTopicRename();
    }
}
