using Microsoft.UI.Xaml;

namespace Glide.App;

public partial class App : Application
{
    private Window? window;
    private readonly SingleInstanceGate? instance;
    public App() : this(null) { }
    internal App(SingleInstanceGate? instance)
    {
        this.instance = instance;
        UnhandledException += (_, e) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "diagnostics");
                Directory.CreateDirectory(folder);
                // Detailed startup diagnostics are opt-in for local development, never the release default.
                string details = Environment.GetEnvironmentVariable("GLIDE_DEV_DIAGNOSTICS") == "1" ? $"\n{e.Message}\n{e.Exception}" : "";
                File.AppendAllText(Path.Combine(folder, "app-errors.log"), $"{DateTimeOffset.UtcNow:O} {e.Exception.GetType().Name} HRESULT={e.Exception.HResult:X8}{details}\n");
            }
            catch (IOException) { /* Diagnostics must not replace the original failure. */ }
            catch (UnauthorizedAccessException) { }
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool development=Environment.GetEnvironmentVariable("GLIDE_DEV_DIAGNOSTICS")=="1";
        bool automated=Environment.GetCommandLineArgs().Contains("--motion-test");
        bool gallery=Environment.GetCommandLineArgs().Contains("--motion-gallery");
        window=development&&(automated||gallery)?new MotionLabWindow(automated):new MainWindow();
        window.Closed += (_, _) => { instance?.StopAccepting(); Exit(); };
        window.Activate();
        instance?.SetActivationHandler(token =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) { completion.TrySetResult(false); return; }
                try { completion.TrySetResult(window is MainWindow main && main.ActivateExisting()); }
                catch (Exception) { completion.TrySetResult(false); }
            })) completion.TrySetResult(false);
            return completion.Task;
        });
    }
}
