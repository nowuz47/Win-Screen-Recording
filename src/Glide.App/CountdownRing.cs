using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Glide.App;

internal sealed class CountdownRing : IDisposable
{
    private readonly FrameworkElement host;
    private readonly MotionPolicy policy;
    private readonly ShapeVisual visual;
    private readonly CompositionEllipseGeometry geometry;
    private readonly CompositionSpriteShape stroke;
    private readonly CompositionColorBrush brush;
    private int remaining = 3;
    public CountdownRing(FrameworkElement host, MotionPolicy policy, Windows.UI.Color color)
    {
        this.host = host; this.policy = policy;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        visual = compositor.CreateShapeVisual(); visual.Size = new(88, 88);
        geometry = compositor.CreateEllipseGeometry(); geometry.Center = new(44, 44); geometry.Radius = new(40, 40);
        brush = compositor.CreateColorBrush(color);
        stroke = compositor.CreateSpriteShape(geometry); stroke.StrokeBrush = brush; stroke.StrokeThickness = 3;
        stroke.CenterPoint = new(44, 44); stroke.RotationAngleInDegrees = -90;
        visual.Shapes.Add(stroke); ElementCompositionPreview.SetElementChildVisual(host, visual);
        policy.Changed += Reduce;
    }
    public void Step(int seconds)
    {
        remaining = seconds; geometry.StopAnimation("TrimEnd"); geometry.TrimEnd = seconds / 3f;
        if (policy.Reduced) return;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(1);
        var linear = visual.Compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(0, seconds / 3f); animation.InsertKeyFrame(1, (seconds - 1) / 3f, linear);
        geometry.StartAnimation("TrimEnd", animation);
    }
    private void Reduce() { geometry.StopAnimation("TrimEnd"); geometry.TrimEnd = remaining / 3f; }
    public void Dispose()
    {
        policy.Changed -= Reduce; geometry.StopAnimation("TrimEnd");
        ElementCompositionPreview.SetElementChildVisual(host, null);
        visual.Shapes.Clear(); // Detached objects are released by COM reference counting, not closed in flight.
    }
}
