using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Glide.App;

internal static class MotionTokens
{
    public const int Press = 120, Hover = 160, Preview = 180, Panel = 220, Page = 280, Exit = 120, Reduced = 80;
    public static readonly Vector2 Enter1 = new(.16f, 1), Enter2 = new(.3f, 1);
    public static readonly Vector2 Exit1 = new(.4f, 0), Exit2 = new(1, 1);
}

// Only owns app chrome, never the media camera or its clock. Commands never await this class.
internal sealed class MotionCoordinator : IDisposable
{
    private sealed class Running
    {
        public required CompositionScopedBatch Batch;
        public required TypedEventHandler<object, CompositionBatchCompletedEventArgs> Handler;
        public readonly List<CompositionAnimation> Animations = [];
        public required Action Settle;
    }
    private readonly MotionPolicy policy;
    // XAML facade properties and GetElementVisual cannot be mixed on the same element.
    private readonly Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
    private readonly Dictionary<UIElement, Running> running = [];
    private readonly Dictionary<Button, Action> buttonCleanup = [];
    private readonly List<ConnectedAnimation> connections = [];
    private bool disposed, visible = true;
    public int ActiveCount => running.Count;
    public int AttachedButtonCount => buttonCleanup.Count;
    public int FallbackCount { get; private set; }
    public bool Reduced => policy.Reduced;
    public MotionCoordinator(MotionPolicy policy) { this.policy = policy; policy.Changed += CancelAll; }
    public void SetVisible(bool value) { visible = value; if (!value) CancelAll(); }

    private void Run(UIElement element, int milliseconds, Action<Running, Compositor, CompositionEasingFunction, int> start,
        Action settle, bool exiting = false)
    {
        // Replacing an animation starts at its current presentation value. Old completions cannot mutate the new state.
        Forget(element, stop: false);
        if (disposed || !visible || element.XamlRoot is null) { settle(); return; }
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Running? entry = null;
        TypedEventHandler<object, CompositionBatchCompletedEventArgs> completed = (_, _) =>
        {
            if (!disposed && running.TryGetValue(element, out var current) && ReferenceEquals(entry, current)) Forget(element, true);
        };
        entry = new() { Batch = batch, Handler = completed, Settle = settle };
        running[element] = entry; batch.Completed += completed;
        var easing = compositor.CreateCubicBezierEasingFunction(exiting ? MotionTokens.Exit1 : MotionTokens.Enter1, exiting ? MotionTokens.Exit2 : MotionTokens.Enter2);
        try
        {
            start(entry, compositor, easing, Reduced ? Math.Min(milliseconds, MotionTokens.Reduced) : milliseconds);
            batch.End();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // A decorative effect must never fail an otherwise valid capture command.
            FallbackCount++; Forget(element, true);
        }
    }
    private static void Scalar(UIElement element, Running entry, Compositor c, string property, float end,
        int duration, CompositionEasingFunction easing, float? initial = null, int delay = 0)
    {
        var animation = c.CreateScalarKeyFrameAnimation(); animation.Target = property;
        animation.Duration = TimeSpan.FromMilliseconds(duration); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (initial is float from) animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, end, easing); entry.Animations.Add(animation); element.StartAnimation(animation);
    }
    private static void Vector(UIElement element, Running entry, Compositor c, string property, Vector3 end,
        int duration, CompositionEasingFunction easing, Vector3? initial = null, int delay = 0)
    {
        var animation = c.CreateVector3KeyFrameAnimation(); animation.Target = property;
        animation.Duration = TimeSpan.FromMilliseconds(duration); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (initial is Vector3 from) animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, end, easing); entry.Animations.Add(animation); element.StartAnimation(animation);
    }
    public void Enter(FrameworkElement element, int duration = MotionTokens.Panel, float x = 0, float y = 12, float scale = 1, int delay = 0)
    {
        bool wasAnimating = running.ContainsKey(element);
        element.CenterPoint = new((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        Run(element, duration, (entry, c, easing, ms) =>
        {
            Scalar(element, entry, c, "Opacity", 1, ms, easing, wasAnimating ? null : 0, Reduced ? 0 : delay);
            if (!Reduced)
            {
                Vector(element, entry, c, "Translation", Vector3.Zero, ms, easing, wasAnimating ? null : new(x, y, 0), delay);
                Vector(element, entry, c, "Scale", Vector3.One, ms, easing, wasAnimating ? null : new(scale, scale, 1), delay);
            }
        }, () => Reset(element));
    }
    public void Exit(FrameworkElement element, Action completed)
    {
        Run(element, MotionTokens.Exit, (entry, c, easing, ms) => Scalar(element, entry, c, "Opacity", 0, ms, easing),
            () => { Reset(element); completed(); }, exiting: true);
    }
    public void Feedback(FrameworkElement element) => Enter(element, MotionTokens.Panel, y: 0, scale: .97f);
    public void Move(FrameworkElement element, float dx, float dy)
    {
        if (Reduced) { Cancel(element); return; }
        Run(element, MotionTokens.Panel, (entry, c, easing, ms) =>
            Vector(element, entry, c, "Translation", Vector3.Zero, ms, easing, new(dx, dy, 0)), () => Reset(element));
    }
    public void AttachButton(Button button, bool card = false)
    {
        if (buttonCleanup.ContainsKey(button)) return;
        bool pointer = false, pressed = false;
        void Update()
        {
            bool highlighted = pointer || button.FocusState == FocusState.Keyboard;
            float scale = !button.IsEnabled || Reduced ? 1 : pressed ? .98f : card && highlighted ? 1.01f : 1;
            float y = !button.IsEnabled || Reduced || !card || !highlighted || pressed ? 0 : -3;
            button.CenterPoint = new((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
            if (Reduced) { Cancel(button); return; }
            Run(button, pressed ? MotionTokens.Press : MotionTokens.Hover, (entry, c, easing, ms) =>
            {
                Vector(button, entry, c, "Scale", new(scale, scale, 1), ms, easing);
                Vector(button, entry, c, "Translation", new(0, y, 0), ms, easing);
            }, () => { button.Scale = new(scale, scale, 1); button.Translation = new(0, y, 0); });
        }
        PointerEventHandler enter = (_, _) => { pointer = true; Update(); };
        PointerEventHandler leave = (_, _) => { pointer = pressed = false; Update(); };
        PointerEventHandler down = (_, _) => { pressed = true; Update(); };
        PointerEventHandler up = (_, _) => { pressed = false; Update(); };
        RoutedEventHandler focus = (_, _) => Update();
        RoutedEventHandler blur = (_, _) => { pressed = false; Update(); };
        KeyEventHandler keyDown = (_, e) => { if (e.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter) { pressed = true; Update(); } };
        KeyEventHandler keyUp = (_, _) => { pressed = false; Update(); };
        button.PointerEntered += enter; button.PointerExited += leave;
        button.AddHandler(UIElement.PointerPressedEvent, down, true); button.AddHandler(UIElement.PointerReleasedEvent, up, true);
        button.PointerCanceled += leave; button.PointerCaptureLost += up;
        button.GotFocus += focus; button.LostFocus += blur;
        button.AddHandler(UIElement.KeyDownEvent, keyDown, true); button.AddHandler(UIElement.KeyUpEvent, keyUp, true);
        buttonCleanup[button] = () =>
        {
            button.PointerEntered -= enter; button.PointerExited -= leave;
            button.RemoveHandler(UIElement.PointerPressedEvent, down); button.RemoveHandler(UIElement.PointerReleasedEvent, up);
            button.PointerCanceled -= leave; button.PointerCaptureLost -= up;
            button.GotFocus -= focus; button.LostFocus -= blur;
            button.RemoveHandler(UIElement.KeyDownEvent, keyDown); button.RemoveHandler(UIElement.KeyUpEvent, keyUp);
        };
    }
    public void DetachButton(Button button)
    {
        Cancel(button); if (buttonCleanup.Remove(button, out var cleanup)) cleanup();
    }
    public void Connect(FrameworkElement source, FrameworkElement destination, Action changeView)
    {
        foreach (var previous in connections) previous.Cancel(); connections.Clear();
        ConnectedAnimation? connection = null;
        if (!Reduced && visible && source.XamlRoot is not null)
        {
            var service = ConnectedAnimationService.GetForCurrentView();
            service.DefaultDuration = TimeSpan.FromMilliseconds(280);
            connection = service.PrepareToAnimate("mode-icon", source);
            connection.Configuration = new DirectConnectedAnimationConfiguration();
        }
        changeView();
        if (connection is null) return;
        connections.Add(connection);
        destination.UpdateLayout();
        if (!connection.TryStart(destination)) connection.Cancel();
    }
    public void Cancel(UIElement element) { Forget(element, true); Reset(element); }
    private void Forget(UIElement element, bool stop)
    {
        if (!running.Remove(element, out var entry)) return;
        entry.Batch.Completed -= entry.Handler;
        foreach (var animation in entry.Animations)
        {
            if (stop) element.StopAnimation(animation);
        }
        // Release managed references instead of IClosable.Close: XAML/compositor may still
        // hold the animation, easing or batch while delivering completion asynchronously.
        if (stop) entry.Settle();
    }
    private static void Reset(UIElement element) { element.Opacity = 1; element.Translation = Vector3.Zero; element.Scale = Vector3.One; }
    public void CancelAll()
    {
        foreach (var element in running.Keys.ToArray()) Forget(element, true);
        foreach (var button in buttonCleanup.Keys) Reset(button);
        foreach (var connection in connections) connection.Cancel(); connections.Clear();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; policy.Changed -= CancelAll; CancelAll();
        foreach (var cleanup in buttonCleanup.Values) cleanup(); buttonCleanup.Clear();
    }
}
