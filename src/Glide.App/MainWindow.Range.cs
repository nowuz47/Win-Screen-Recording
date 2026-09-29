using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glide.App;

public sealed partial class MainWindow
{
    private PointerDrag? startRangeDrag, endRangeDrag;

    private void InitializeRange()
    {
        PointerDrag Attach(Control handle, bool start)
        {
            double originX = 0, originValue = 0, pixelsPerSecond = 1;
            return new(handle,
            e => e.GetCurrentPoint(RangeTrack).Position,
            point =>
            {
                if (busy || loadingProject || renderer is null || RangeTrack.ActualWidth <= 0 || CutEnd.Maximum <= 0) return false;
                originValue = start ? CutStart.Value : CutEnd.Value;
                if (!double.IsFinite(originValue)) return false;
                originX = point.X;
                pixelsPerSecond = RangeTrack.ActualWidth / CutEnd.Maximum;
                return true;
            }, point =>
            {
                ChangeRange(start, originValue + (point.X - originX) / pixelsPerSecond);
            }, _ => { });
        }
        startRangeDrag = Attach(RangeStart, true);
        endRangeDrag = Attach(RangeEnd, false);
    }
    private void RangeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        startRangeDrag?.Cancel(); endRangeDrag?.Cancel(); DrawRange();
    }
    private void RangeValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => DrawRange();
    private void DrawRange()
    {
        if (RangeTrack is null || RangeFill is null || CutStart is null || CutEnd is null || CutEnd.Maximum <= 0) return;
        double width = RangeTrack.ActualWidth;
        if (width <= 0 || !double.IsFinite(CutStart.Value) || !double.IsFinite(CutEnd.Value)) return;
        double start = Math.Clamp(CutStart.Value / CutEnd.Maximum, 0, 1) * width;
        double end = Math.Clamp(CutEnd.Value / CutEnd.Maximum, 0, 1) * width;
        Canvas.SetLeft(RangeStart, start - 12); Canvas.SetLeft(RangeEnd, end - 12);
        Canvas.SetLeft(RangeFill, start); RangeFill.Width = Math.Max(0, end - start);
        ToolTipService.SetToolTip(RangeStart, $"시작 {CutStart.Value:0.00}초");
        ToolTipService.SetToolTip(RangeEnd, $"끝 {CutEnd.Value:0.00}초");
        AutomationProperties.SetName(RangeStart, $"선택 시작 {CutStart.Value:0.00}초");
        AutomationProperties.SetName(RangeEnd, $"선택 끝 {CutEnd.Value:0.00}초");
    }
    private void ChangeRange(bool start, double value)
    {
        if (busy || loadingProject || renderer is null || !double.IsFinite(value)) return;
        StopPlayback();
        if (start) CutStart.Value = Math.Clamp(Math.Round(value, 2), 0, Math.Max(0, CutEnd.Value - .01));
        else CutEnd.Value = Math.Clamp(Math.Round(value, 2), Math.Min(CutEnd.Maximum, CutStart.Value + .01), CutEnd.Maximum);
        DrawRange();
    }
    private void RangeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool start = ReferenceEquals(sender, RangeStart);
        double current = start ? CutStart.Value : CutEnd.Value;
        double? next = e.Key switch
        {
            Windows.System.VirtualKey.Left => current - .01,
            Windows.System.VirtualKey.Right => current + .01,
            Windows.System.VirtualKey.Home => start ? 0 : CutStart.Value + .01,
            Windows.System.VirtualKey.End => start ? CutEnd.Value - .01 : CutEnd.Maximum,
            _ => null
        };
        if (next is null) return;
        ChangeRange(start, next.Value); e.Handled = true;
    }
}
