using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Glide.App;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
    {
        bool lab = Environment.GetEnvironmentVariable("GLIDE_DEV_DIAGNOSTICS") == "1"
            && (args.Contains("--motion-test") || args.Contains("--motion-gallery"));
        try
        {
            // The development compositor lab creates no projects or global hotkeys.
            using var instance = lab ? null : new SingleInstanceGate(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide"));
            if (instance is not null && !instance.TryAcquire())
            {
                var result = instance.ActivateExistingAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (result == ActivationResult.Activated) return 0;
                // The owner can exit during redirection. Only a newly acquired
                // kernel lease permits us to proceed with startup after that race.
                if (!instance.TryAcquire())
                {
                    MessageBoxW(0, result == ActivationResult.OtherSession
                        ? "다른 Windows 세션에서 Glide가 실행 중입니다. 해당 세션에서 작업을 마친 뒤 다시 실행해 주세요."
                        : "Glide가 이미 실행 중이거나 작업을 마치는 중입니다. 기존 창을 확인한 뒤 잠시 후 다시 실행해 주세요.", "Glide", 0x40);
                    return 2;
                }
            }
            instance?.StartListening();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(instance);
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(0, $"Glide를 시작하지 못했습니다. 진행 중인 작업을 확인하고 다시 실행해 주세요. ({ex.HResult:X8})", "Glide", 0x10);
            return 1;
        }
    }
}
