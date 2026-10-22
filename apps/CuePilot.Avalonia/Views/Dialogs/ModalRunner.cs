using Avalonia.Controls;
using Avalonia.Threading;

namespace CuePilot.Avalonia.Views.Dialogs;

/// <summary>
/// The shared view model asks its questions synchronously (Func&lt;bool&gt;), as WPF ShowDialog does.
/// Avalonia dialogs are asynchronous, so the UI thread keeps pumping events in a nested frame until the dialog closes:
/// the window stays responsive and the caller still gets its answer in place.
/// </summary>
internal static class ModalRunner
{
    public static void Run(Window dialog, Window? owner)
    {
        var frame = new DispatcherFrame();
        dialog.Closed += (_, _) => frame.Continue = false;
        if (owner is { IsVisible: true })
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        Dispatcher.UIThread.PushFrame(frame);
    }
}
