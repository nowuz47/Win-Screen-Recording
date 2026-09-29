using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Glide.App;

// Use the event's position, not the cursor's later position when the UI thread
// handles it. The caller supplies a space that does not move with the handle.
internal sealed class PointerDrag
{
    private readonly Control handle;
    private readonly Func<PointerRoutedEventArgs, Point> position;
    private readonly Func<Point, bool> start;
    private readonly Action<Point> move;
    private readonly Action<bool> complete;
    private Pointer? pointer;

    public PointerDrag(Control handle, Func<PointerRoutedEventArgs, Point> position,
        Func<Point, bool> start, Action<Point> move, Action<bool> complete)
    {
        this.handle = handle; this.position = position;
        this.start = start; this.move = move; this.complete = complete;
        handle.PointerPressed += Pressed;
        handle.PointerMoved += Moved;
        handle.PointerReleased += Released;
        handle.PointerCanceled += Canceled;
        handle.PointerCaptureLost += Canceled;
        handle.Unloaded += (_, _) => Cancel();
        handle.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && pointer is not null)
            { Cancel(); e.Handled = true; }
        };
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (pointer is not null || !handle.IsEnabled ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        handle.Focus(FocusState.Pointer);
        if (!handle.CapturePointer(e.Pointer)) return;
        pointer = e.Pointer;
        if (!start(position(e))) { Cancel(); return; }
        e.Handled = true;
    }
    private bool Owns(PointerRoutedEventArgs e) => pointer?.PointerId == e.Pointer.PointerId;
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!Owns(e) || e.IsGenerated) return;
        move(position(e)); e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!Owns(e) || e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        move(position(e)); Finish(false); e.Handled = true;
    }
    private void Canceled(object sender, PointerRoutedEventArgs e)
    { if (Owns(e)) Finish(true); }
    public void Cancel() => Finish(true);
    private void Finish(bool canceled)
    {
        if (pointer is null) return;
        var previous = pointer; pointer = null;
        handle.ReleasePointerCapture(previous);
        complete(canceled);
    }
}
