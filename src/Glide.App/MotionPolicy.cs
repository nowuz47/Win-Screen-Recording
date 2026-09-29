using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace Glide.App;

internal sealed class MotionPolicy : IDisposable
{
    private readonly UISettings system = new();
    private readonly DispatcherQueue dispatcher;
    private bool disposed;
    private UiMotion preference;
    public bool Reduced => UiPreferences.ReducesMotion(preference, system.AnimationsEnabled);
    public event Action? Changed;
    public MotionPolicy(DispatcherQueue dispatcher, UiMotion preference)
    {
        this.dispatcher = dispatcher; this.preference = preference;
        system.AnimationsEnabledChanged += SystemChanged;
    }
    public void SetPreference(UiMotion next)
    {
        if (preference == next) return;
        preference = next; Changed?.Invoke();
    }
    private void SystemChanged(UISettings sender, object args) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed) Changed?.Invoke();
    });
    public void Dispose()
    {
        disposed = true; system.AnimationsEnabledChanged -= SystemChanged; Changed = null;
    }
}
