using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CuePilotDesktop.Services;
using CuePilotDesktop.Views;
using CuePilotDesktop.Views.Controls;

namespace CuePilotDesktop;

/// <summary>Topic tabs: the note editor, the question box, the dialog scrolling and the graph details card.</summary>
public partial class MainWindow
{
    private static readonly string[] GraphTypes = ["person", "organization", "project", "task", "decision", "document", "date", "place", "term"];

    private void InitializeTopicWorkspaceUi()
    {
        _viewModel.NoteEditor = (title, text) => EchoNoteDialog.Show(this, _viewModel.L, title, text);
        _viewModel.QuestionItems.CollectionChanged += QuestionItems_OnCollectionChanged;
        _viewModel.TopicGraphChanged += () => Dispatcher.BeginInvoke(CloseGraphDetails);
        TopicGraphView.NodeSelected += ShowGraphDetails;
        TopicGraphView.LayoutChanged += _viewModel.SaveGraphLayout;
        PreviewKeyDown += KnowledgePaste_OnPreviewKeyDown;
        UpdateGraphTypeNames();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.SelectedUiLanguage))
            {
                UpdateGraphTypeNames();
            }
            else if (e.PropertyName == nameof(ViewModels.MainViewModel.ShowQuestionsPanel) && _viewModel.ShowQuestionsPanel)
            {
                Dispatcher.BeginInvoke(() => TopicQuestionTextBox.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
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
            Dispatcher.BeginInvoke(
                () => QuestionsList.ScrollIntoView(_viewModel.QuestionItems[0]),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Delete removes the selected source, or the checked ones while selecting several.</summary>
    private void KnowledgeList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
            return;
        }

        ICommand command = _viewModel.IsKnowledgeSelectionMode
            ? _viewModel.RemoveCheckedKnowledgeCommand
            : _viewModel.RemoveKnowledgeItemCommand;
        if (command.CanExecute(null))
        {
            e.Handled = true;
            command.Execute(null);
        }
    }

    /// <summary>Files, folders and text from Explorer, a browser or a mail; a session card dragged by mistake is refused.</summary>
    private static bool CanAddToKnowledge(IDataObject data) =>
        !data.GetDataPresent(SessionDragFormat) &&
        (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText, true));

    /// <summary>Copied or dragged files and folders each become a source, text becomes a note.</summary>
    private bool TryAddToKnowledge(IDataObject? data)
    {
        if (data == null || !CanAddToKnowledge(data))
        {
            return false;
        }

        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _ = _viewModel.AddKnowledgePathsAsync(paths);
            return true;
        }

        if (data.GetData(DataFormats.UnicodeText, true) is string text && !string.IsNullOrWhiteSpace(text))
        {
            _ = _viewModel.AddKnowledgeTextAsync(text);
            return true;
        }

        return false;
    }

    // Preview events: the preview pane's text view would otherwise take dropped text for itself.
    private void KnowledgePanel_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool accepts = CanAddToKnowledge(e.Data);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        KnowledgeDropOverlay.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void KnowledgePanel_OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // Moving between elements inside the panel raises DragLeave too: hide only when the cursor has left it.
        Point point = e.GetPosition(KnowledgePanel);
        if (point.X < 0 || point.Y < 0 || point.X >= KnowledgePanel.ActualWidth || point.Y >= KnowledgePanel.ActualHeight)
        {
            KnowledgeDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void KnowledgePanel_OnPreviewDrop(object sender, DragEventArgs e)
    {
        KnowledgeDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = TryAddToKnowledge(e.Data);
    }

    /// <summary>
    /// Ctrl+V with the mouse over «База знаний» (or the focus inside it) adds what was copied. A text box that is
    /// being typed in keeps its own paste.
    /// </summary>
    private void KnowledgePaste_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !KnowledgePanel.IsVisible ||
            !(KnowledgePanel.IsMouseOver || KnowledgePanel.IsKeyboardFocusWithin) ||
            Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false })
        {
            return;
        }

        e.Handled = true;
        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another program is holding the clipboard open.
            _viewModel.AppendLog("[knowledge] Clipboard read failed: " + ex.Message);
            _viewModel.ShowFeedback(_viewModel.L["KnowledgePasteFailed"]);
            return;
        }

        if (!TryAddToKnowledge(data))
        {
            _viewModel.ShowFeedback(_viewModel.L["KnowledgePasteEmpty"]);
        }
    }

    /// <summary>Enter sends the question, Shift+Enter starts a new line.</summary>
    private void TopicQuestionTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
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
        GraphDetailsDescription.Visibility = string.IsNullOrWhiteSpace(node.Description) ? Visibility.Collapsed : Visibility.Visible;
        GraphDetailsLinks.ItemsSource = TopicGraphView.GetLinks(node)
            .Select(link => new GraphLinkRow(link.Label, link.Relation, link.Node))
            .ToList();
        GraphDetailsSources.Text = node.Sources.Count == 0
            ? string.Empty
            : _viewModel.L["GraphDetailsSources"] + ": " + string.Join(", ", node.Sources);
        GraphDetailsSources.Visibility = node.Sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        GraphDetailsPanel.Visibility = Visibility.Visible;
    }

    private void CloseGraphDetails()
    {
        GraphDetailsPanel.Visibility = Visibility.Collapsed;
        TopicGraphView.SelectNode(null);
    }

    private void GraphDetailsClose_OnClick(object sender, RoutedEventArgs e)
    {
        CloseGraphDetails();
    }

    private void GraphDetailsLink_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is GraphNodeData node)
        {
            TopicGraphView.SelectNode(node);
            ShowGraphDetails(node);
        }
    }

    private sealed record GraphLinkRow(string Label, string Relation, GraphNodeData Node);
}
