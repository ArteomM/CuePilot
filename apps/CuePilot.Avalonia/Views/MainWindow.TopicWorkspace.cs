using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CuePilot.Avalonia.Views.Controls;
using CuePilot.Avalonia.Views.Dialogs;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views;

/// <summary>Topic tabs: the note editor, the question box, the dialog scrolling and the graph details card.</summary>
public partial class MainWindow
{
    private static readonly string[] GraphTypes = ["person", "organization", "project", "task", "decision", "document", "date", "place", "term"];

    private void InitializeTopicWorkspaceUi()
    {
        _viewModel.NoteEditor = (title, text) => Dispatcher.UIThread.Invoke(() => EchoNoteDialog.Show(this, _viewModel.L, title, text));
        _viewModel.QuestionItems.CollectionChanged += QuestionItems_OnCollectionChanged;
        _viewModel.TopicGraphChanged += () => Dispatcher.UIThread.Post(CloseGraphDetails);
        TopicGraphView.NodeSelected += ShowGraphDetails;
        TopicGraphView.LayoutChanged += _viewModel.SaveGraphLayout;
        AddHandler(KeyDownEvent, KnowledgePaste_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        KnowledgeList.AddHandler(KeyDownEvent, KnowledgeList_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TopicQuestionTextBox.AddHandler(KeyDownEvent, TopicQuestionTextBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Tunnel: the preview pane's text view would otherwise take dropped text for itself.
        KnowledgePanel.AddHandler(DragDrop.DragEnterEvent, KnowledgePanel_OnPreviewDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        KnowledgePanel.AddHandler(DragDrop.DragOverEvent, KnowledgePanel_OnPreviewDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        KnowledgePanel.AddHandler(DragDrop.DragLeaveEvent, KnowledgePanel_OnPreviewDragLeave, RoutingStrategies.Bubble);
        KnowledgePanel.AddHandler(DragDrop.DropEvent, KnowledgePanel_OnPreviewDrop, RoutingStrategies.Tunnel);
        GraphDetailsCloseButton.Click += (_, _) => CloseGraphDetails();
        GraphDetailsLinks.AddHandler(Button.ClickEvent, GraphDetailsLink_OnClick);
        UpdateGraphTypeNames();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CuePilotDesktop.ViewModels.MainViewModel.SelectedUiLanguage))
            {
                UpdateGraphTypeNames();
            }
            else if (e.PropertyName == nameof(CuePilotDesktop.ViewModels.MainViewModel.ShowQuestionsPanel) && _viewModel.ShowQuestionsPanel)
            {
                Dispatcher.UIThread.Post(() => TopicQuestionTextBox.Focus(), DispatcherPriority.Loaded);
            }
        };
    }

    private void UpdateGraphTypeNames()
    {
        TopicGraphView.TypeNames = GraphTypes.ToDictionary(type => type, type => _viewModel.L["GraphType_" + type]);
        TopicGraphView.InvalidateVisual();
    }

    /// <summary>A new question goes on top of the list: show it.</summary>
    private void QuestionItems_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && _viewModel.QuestionItems.Count > 0)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_viewModel.QuestionItems.Count > 0)
                    {
                        QuestionsList.ScrollIntoView(_viewModel.QuestionItems[0]);
                    }
                },
                DispatcherPriority.Background);
        }
    }

    /// <summary>Delete (or ⌫ on a Mac keyboard) removes the selected source, or the checked ones while selecting several.</summary>
    private void KnowledgeList_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key != Key.Delete && e.Key != Key.Back) || e.Source is TextBox)
        {
            return;
        }

        System.Windows.Input.ICommand command = _viewModel.IsKnowledgeSelectionMode
            ? _viewModel.RemoveCheckedKnowledgeCommand
            : _viewModel.RemoveKnowledgeItemCommand;
        if (command.CanExecute(null))
        {
            e.Handled = true;
            command.Execute(null);
        }
    }

    /// <summary>Files, folders and text from Finder, a browser or a mail; a session card dragged by mistake is refused.</summary>
    private static bool CanAddToKnowledge(IDataObject data) =>
        !data.Contains(SessionDragFormat) &&
        (data.Contains(DataFormats.Files) || data.Contains(DataFormats.Text));

    /// <summary>Copied or dragged files and folders each become a source, text becomes a note.</summary>
    private bool TryAddToKnowledge(IEnumerable<IStorageItem>? items, string? text)
    {
        string[] paths = (items ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            _ = _viewModel.AddKnowledgePathsAsync(paths);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            _ = _viewModel.AddKnowledgeTextAsync(text);
            return true;
        }

        return false;
    }

    private void KnowledgePanel_OnPreviewDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(SessionDragFormat))
        {
            return;
        }

        bool accepts = CanAddToKnowledge(e.Data);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        KnowledgeDropOverlay.IsVisible = accepts;
        e.Handled = true;
    }

    private void KnowledgePanel_OnPreviewDragLeave(object? sender, DragEventArgs e)
    {
        // Moving between elements inside the panel raises DragLeave too: hide only when the cursor has left it.
        Point point = e.GetPosition(KnowledgePanel);
        if (point.X < 0 || point.Y < 0 || point.X >= KnowledgePanel.Bounds.Width || point.Y >= KnowledgePanel.Bounds.Height)
        {
            KnowledgeDropOverlay.IsVisible = false;
        }
    }

    private void KnowledgePanel_OnPreviewDrop(object? sender, DragEventArgs e)
    {
        KnowledgeDropOverlay.IsVisible = false;
        if (CanAddToKnowledge(e.Data))
        {
            e.Handled = TryAddToKnowledge(e.Data.GetFiles(), e.Data.GetText());
        }
    }

    /// <summary>
    /// ⌘V (Ctrl+V elsewhere) with the mouse over «База знаний» (or the focus inside it) adds what was copied.
    /// A text box that is being typed in keeps its own paste.
    /// </summary>
    private async void KnowledgePaste_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool isPaste = PlatformSettings?.HotkeyConfiguration.Paste.Any(gesture => gesture.Matches(e)) == true;
        if (!isPaste || !KnowledgePanel.IsEffectivelyVisible ||
            !(KnowledgePanel.IsPointerOver || KnowledgePanel.IsKeyboardFocusWithin) ||
            FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false })
        {
            return;
        }

        e.Handled = true;
        IEnumerable<IStorageItem>? files = null;
        string? text = null;
        try
        {
            if (Clipboard != null)
            {
                files = await Clipboard.GetDataAsync(DataFormats.Files) as IEnumerable<IStorageItem>;
                text = await Clipboard.GetTextAsync();
            }
        }
        catch (Exception ex)
        {
            // Another program is holding the clipboard.
            _viewModel.AppendLog("[knowledge] Clipboard read failed: " + ex.Message);
            _viewModel.ShowFeedback(_viewModel.L["KnowledgePasteFailed"]);
            return;
        }

        if (!TryAddToKnowledge(files, text))
        {
            _viewModel.ShowFeedback(_viewModel.L["KnowledgePasteEmpty"]);
        }
    }

    /// <summary>Enter sends the question, Shift+Enter starts a new line.</summary>
    private void TopicQuestionTextBox_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.AskTopicCommand.CanExecute(null))
        {
            _viewModel.AskTopicCommand.Execute(null);
        }
    }

    private void ShowGraphDetails(GraphNodeData? node)
    {
        if (node == null)
        {
            CloseGraphDetails();
            return;
        }

        string type = node.Type.ToLowerInvariant();
        GraphDetailsDot.Fill = new SolidColorBrush(GraphView.ColorFor(type));
        GraphDetailsType.Text = TopicGraphView.TypeNames.TryGetValue(type, out string? name) ? name : type;
        GraphDetailsTitle.Text = node.Label;
        GraphDetailsDescription.Text = node.Description;
        GraphDetailsDescription.IsVisible = !string.IsNullOrWhiteSpace(node.Description);
        GraphDetailsLinks.ItemsSource = TopicGraphView.GetLinks(node)
            .Select(link => new GraphLinkRow(link.Label, link.Relation, link.Node))
            .ToList();
        GraphDetailsSources.Text = node.Sources.Count == 0
            ? string.Empty
            : _viewModel.L["GraphDetailsSources"] + ": " + string.Join(", ", node.Sources);
        GraphDetailsSources.IsVisible = node.Sources.Count > 0;
        GraphDetailsPanel.IsVisible = true;
    }

    private void CloseGraphDetails()
    {
        GraphDetailsPanel.IsVisible = false;
        TopicGraphView.SelectNode(null);
    }

    private void GraphDetailsLink_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true)?.Tag is GraphNodeData node)
        {
            TopicGraphView.SelectNode(node);
            ShowGraphDetails(node);
        }
    }

    private sealed record GraphLinkRow(string Label, string Relation, GraphNodeData Node);
}
