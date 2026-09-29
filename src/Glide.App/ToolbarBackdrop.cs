using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glide.App;

// One controller per toolbar. System configuration retains high-contrast,
// transparency and power-policy fallbacks; the text/buttons stay opaque.
internal sealed class ToolbarBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    private SystemBackdropConfiguration? configuration;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        controller = new DesktopAcrylicController();
        configuration = new SystemBackdropConfiguration { IsInputActive = true };
        CopySystemConfiguration(target, root);
        controller.SetSystemBackdropConfiguration(configuration);
        controller.TintColor = Color.FromArgb(255, 24, 29, 36);
        controller.TintOpacity = .72f;
        controller.LuminosityOpacity = .38f;
        controller.FallbackColor = Color.FromArgb(255, 29, 34, 41);
        controller.AddSystemBackdropTarget(target);
    }
    private void CopySystemConfiguration(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        if (configuration is null) return;
        var defaults = GetDefaultSystemBackdropConfiguration(target, root);
        configuration.Theme = SystemBackdropTheme.Dark;
        configuration.IsHighContrast = defaults.IsHighContrast;
        configuration.HighContrastBackgroundColor = defaults.HighContrastBackgroundColor;
        // A floating control remains glass while the captured app has focus.
        // Transparency/power policies remain enforced by DesktopAcrylicController.
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        CopySystemConfiguration(target, root);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose(); controller = null; configuration = null;
        base.OnTargetDisconnected(target);
    }
}
