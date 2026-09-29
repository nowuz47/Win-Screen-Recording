using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Glide.App;

internal enum ActivationResult : byte { Activated = 1, Closing = 2, OtherSession = 3, Unavailable = 4 }

// Acquire and dispose on the entry-point thread. Hold the mutex until the UI loop
// and its cleanup have exited; a visible window alone is not ownership evidence.
[SupportedOSPlatform("windows")]
internal sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex ownership;
    private readonly string pipeName;
    private readonly uint sessionId;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource<Func<CancellationToken, Task<bool>>> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? server;
    private Task? listener;
    private bool owned, disposed;
    private const byte ActivateCommand = 0x47;
    internal string PipeName => pipeName;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public SingleInstanceGate(string profileDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string account = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity unavailable.");
        using var process = Process.GetCurrentProcess();
        sessionId = (uint)process.SessionId;
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant();
        string key = "Glide.Profile." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account + "|" + normalized)));
        ownership = new Mutex(key, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        pipeName = key + ".activate.v1";
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owned) return true;
        try { owned = ownership.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // Kernel releases a crashed owner's lease.
        return owned;
    }

    public void StartListening()
    {
        if (!owned || listener is not null) throw new InvalidOperationException("Primary lease required once.");
        server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 16, 16);
        listener = ListenAsync(server);
    }

    public void SetActivationHandler(Func<CancellationToken, Task<bool>> handler) => ready.SetResult(handler);
    public void StopAccepting() => shutdown.Cancel();

    private async Task ListenAsync(NamedPipeServerStream pipe)
    {
        while (!shutdown.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                connected = true;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] command = new byte[1];
                await pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                ActivationResult result = ActivationResult.Unavailable;
                if (command[0] == ActivateCommand && GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session))
                {
                    if (session != sessionId) result = ActivationResult.OtherSession;
                    else
                    {
                        var handler = await ready.Task.WaitAsync(request.Token).ConfigureAwait(false);
                        result = await handler(request.Token).WaitAsync(request.Token).ConfigureAwait(false)
                            ? ActivationResult.Activated : ActivationResult.Closing;
                    }
                }
                await pipe.WriteAsync(new[] { (byte)result }, request.Token).ConfigureAwait(false);
                // Disconnect discards unread outbound pipe data. Keep the bounded
                // connection alive until the launcher has actually read the reply.
                // WaitForPipeDrain would block without a cancellation deadline.
                byte[] receipt = new byte[1];
                await pipe.ReadExactlyAsync(receipt, request.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            { /* A dead, slow or malformed launcher must not stop recording or end the listener. */ }
            finally
            {
                // IsConnected becomes false after a peer closes unexpectedly, but
                // a successfully accepted pipe still needs Disconnect to reset it.
                if (connected)
                {
                    try { pipe.Disconnect(); } catch (IOException) { }
                }
            }
        }
    }

    public async Task<ActivationResult> ActivateExistingAsync(TimeSpan timeout)
    {
        if (owned) throw new InvalidOperationException("The primary cannot redirect to itself.");
        using var cancel = new CancellationTokenSource(timeout);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(cancel.Token).ConfigureAwait(false);
            // Use the OS-reported server PID, never a PID or window handle supplied
            // in an IPC payload. Foreground policy may still flash the taskbar.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint process)) AllowSetForegroundWindow(process);
            await pipe.WriteAsync(new[] { ActivateCommand }, cancel.Token).ConfigureAwait(false);
            byte[] response = new byte[1];
            await pipe.ReadExactlyAsync(response, cancel.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 0x06 }, cancel.Token).ConfigureAwait(false);
            return response[0] is >= 1 and <= 4 ? (ActivationResult)response[0] : ActivationResult.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        { return ActivationResult.Unavailable; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown.Cancel();
        // Every server wait and the UI acknowledgement observes shutdown, so this
        // cannot wait for a UI callback on the thread that is exiting the UI loop.
        listener?.GetAwaiter().GetResult();
        server?.Dispose(); shutdown.Dispose();
        if (owned) { owned = false; ownership.ReleaseMutex(); }
        ownership.Dispose();
    }
}
