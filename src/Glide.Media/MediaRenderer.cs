using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Glide.Core;

namespace Glide.Media;

public sealed record RenderProgress(long Completed, long Total);
public sealed record RenderResult(string Path, long Revision, long Frames, long DurationUs, string Sha256,
    bool HasAudio = false, long AudioFrames = 0, long AudioDurationUs = 0, long ClippedAudioSamples = 0);

// One immutable edit plus read leases on verified media. Call rendering methods on an MTA worker.
// A private lock serializes preview/export/disposal; UI code cancels obsolete preview requests.
public sealed class MediaRenderer : IDisposable
{
    private readonly object gate = new();
    private readonly RenderPlan plan;
    private readonly RenderPlan originalPlan;
    private ulong previewSession;
    private readonly ProjectDocument project;
    private readonly SegmentRecord[] segments;
    private readonly string[] paths;
    private readonly List<FileStream> leases = [];
    private AudioMixer? audio;
    private bool disposed;
    public RenderPlan Plan => plan;

    public MediaRenderer(ProjectStore store, ProjectDocument project, CursorTrack cursors, int width = 1920, int height = 1080, int fps = 30, RenderStyle? style = null)
    {
        this.project = project = project with { Ranges = project.Ranges.ToArray(), Zooms = project.Zooms.ToArray(), Audio = project.Audio?.ToArray() };
        var presentation = project.Origin == "presentation" ? CaptureImport.ReadPresentation(Path.Combine(store.ProjectPath(project.Id), "capture"), project.DurationUs) : null;
        plan = new(project, cursors, width, height, fps, style, presentation);
        originalPlan = new(project with { Zooms = [], UsePresentationCamera = false }, cursors, width, height, fps,
            new(Padding: 0, CursorScale: 1, SmoothCursor: false, ShowClicks: false));
        if (Native.AbiVersion() != 2 || Marshal.SizeOf<Native.Frame>() != 152 || Marshal.SizeOf<Native.Source>() != 16)
            throw new InvalidOperationException("Unsupported render ABI.");
        var recovery = store.Recover(project.Id);
        if (recovery.Issues.Count != 0 || recovery.Segments.Count == 0) throw new InvalidDataException("Source media requires recovery before rendering.");
        segments = recovery.Segments.ToArray();
        paths = segments.Select(s => Path.Combine(store.ProjectPath(project.Id), "media", s.FileName)).ToArray();
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                var file = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                leases.Add(file);
                if (file.Length != segments[i].Bytes || Convert.ToHexString(SHA256.HashData(file)) != segments[i].Sha256)
                    throw new InvalidDataException("Media changed while preparing render.");
            }
            // Validate complete retained ranges, including holes shorter than one output frame.
            foreach (var range in project.Ranges)
            {
                long cursor = range.StartUs;
                foreach (var segment in segments)
                {
                    if (segment.EndUs <= cursor) continue;
                    if (segment.StartUs > cursor || cursor >= range.EndUs) break;
                    cursor = Math.Min(range.EndUs, segment.EndUs);
                }
                if (cursor != range.EndUs) throw new InvalidDataException("Edit references missing media.");
            }
            audio = new AudioMixer(store, project);
            if (audio.IsAudible && Native.AudioAbiVersion() != 1) throw new InvalidOperationException("Unsupported audio render ABI.");
        }
        catch { Dispose(); throw; }
    }

    private Native.Frame Map(RenderFrame f)
    {
        int index = Array.FindIndex(segments, s => s.StartUs <= f.SourceUs && f.SourceUs < s.EndUs);
        if (index < 0) throw new InvalidDataException("No source frame for this time.");
        return new Native.Frame
        {
            SourceUs = f.SourceUs - segments[index].StartUs, OutputUs = f.OutputUs,
            CropX = f.SourceCrop.X, CropY = f.SourceCrop.Y, CropW = f.SourceCrop.Width, CropH = f.SourceCrop.Height,
            DestX = f.Destination.X, DestY = f.Destination.Y, DestW = f.Destination.Width, DestH = f.Destination.Height,
            CursorX = f.CursorPixel.X, CursorY = f.CursorPixel.Y, CursorScale = f.CursorScale,
            CursorVisible = f.CursorVisible ? 1u : 0u, SourceIndex = (uint)index,
            Background = f.Background, CornerRadius = f.CornerRadius, ClickAmount = f.ClickAmount, ClickX = f.ClickPixel.X, ClickY = f.ClickPixel.Y
        };
    }

    public byte[] Preview(long outputUs, bool original = false)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var frame = Map((original ? originalPlan : plan).AtTime(outputUs));
            byte[] pixels = new byte[checked(plan.Width * plan.Height * 4)];
            if (previewSession == 0) CheckNative(Native.PreviewCreate((uint)project.Width, (uint)project.Height, (uint)plan.Width, (uint)plan.Height, out previewSession));
            CheckNative(Native.PreviewFrame(previewSession, paths[frame.SourceIndex], in frame, pixels, (uint)pixels.Length));
            return pixels;
        }
    }

    public RenderResult Export(string output, CancellationToken cancellation = default, Action<RenderProgress>? progress = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            output = Path.GetFullPath(output);
            if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("MP4 output is required.");
            if (File.Exists(output)) throw new IOException("Output already exists.");
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("Output directory does not exist.");
            string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".glide-render-" + Guid.NewGuid().ToString("N") + ".mp4");
            var sources = paths.Select((_, i) => new Native.Source { DurationUs = segments[i].EndUs - segments[i].StartUs }).ToArray();
            try
            {
                var frames = new Native.Frame[plan.FrameCount];
                for (int i = 0; i < frames.Length; i++) { cancellation.ThrowIfCancellationRequested(); frames[i] = Map(plan.AtFrame(i)); }
                for (int i = 0; i < sources.Length; i++) sources[i].Path = Marshal.StringToCoTaskMemUni(paths[i]);
                Exception? callbackFailure = null;
                long clippedAudioSamples = 0;
                Native.Progress callback = (done, total) =>
                {
                    try { progress?.Invoke(new(done, total)); return cancellation.IsCancellationRequested ? 1 : 0; }
                    catch (Exception e) { callbackFailure = e; return 1; }
                };
                short[] pcm = new short[3200];
                Native.Audio? audioCallback = audio!.IsAudible ? (first, count, buffer) =>
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (count == 0 || count > 1600 || buffer == 0) throw new InvalidDataException("Invalid audio render request.");
                        var mixed = audio.Read(first, pcm.AsSpan(0, checked((int)count * 2)));
                        clippedAudioSamples += mixed.ClippedSamples;
                        Marshal.Copy(pcm, 0, buffer, (int)count * 2);
                        return 0;
                    }
                    catch (Exception e) { callbackFailure = e; return e.HResult < 0 ? e.HResult : unchecked((int)0x80004005); }
                } : null;
                int result = audioCallback is null ?
                    Native.RenderVideo(temporary, sources, (uint)sources.Length, frames, (uint)frames.Length,
                        (uint)project.Width, (uint)project.Height, (uint)plan.Width, (uint)plan.Height, (uint)plan.Fps, plan.DurationUs, callback) :
                    Native.RenderVideoWithAudio(temporary, sources, (uint)sources.Length, frames, (uint)frames.Length,
                        (uint)project.Width, (uint)project.Height, (uint)plan.Width, (uint)plan.Height, (uint)plan.Fps, plan.DurationUs, callback, audioCallback);
                GC.KeepAlive(callback);
                GC.KeepAlive(audioCallback);
                if (callbackFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                cancellation.ThrowIfCancellationRequested();
                CheckNative(result);
                Marshal.ThrowExceptionForHR(Native.Verify(temporary, out ulong decoded, out long duration));
                if (decoded != (ulong)plan.FrameCount || Math.Abs(duration - plan.DurationUs) > 1_000_000 / plan.Fps)
                    throw new InvalidDataException("Encoded output failed frame/duration verification.");
                ulong audioFrames = 0; long audioDuration = 0;
                if (audioCallback is not null)
                {
                    Marshal.ThrowExceptionForHR(Native.VerifyAudio(temporary, out audioFrames, out audioDuration));
                    // AAC encodes blocks of 1024 samples. This structural check is
                    // separate from independent signal alignment/quality validation.
                    if (Math.Abs(audioDuration - plan.DurationUs) > 50_000 || Math.Abs((long)audioFrames - audio.FrameCount) > 2400)
                        throw new InvalidDataException("Encoded audio failed frame/duration verification.");
                }
                string digest;
                using (var file = File.OpenRead(temporary)) digest = Convert.ToHexString(SHA256.HashData(file));
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, output, overwrite: false);
                return new(output, project.Revision, (long)decoded, duration, digest, audioCallback is not null, (long)audioFrames, audioDuration, clippedAudioSamples);
            }
            finally
            {
                foreach (var source in sources) if (source.Path != 0) Marshal.FreeCoTaskMem(source.Path);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static void CheckNative(int result)
    {
        if (result < 0) throw new COMException($"Rendering failed at stage {Native.ErrorStage()} ({result:X8}).", result);
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; if (previewSession != 0) { Native.PreviewRelease(previewSession); previewSession = 0; } audio?.Dispose(); foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
    }
}

internal static class Native
{
    private const string Dll = "Glide.Capture.dll";
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public nint Path; public long DurationUs; }
    [StructLayout(LayoutKind.Sequential)] internal struct Frame
    {
        public long SourceUs, OutputUs;
        public double CropX, CropY, CropW, CropH, DestX, DestY, DestW, DestH, CursorX, CursorY, CursorScale;
        public uint CursorVisible, SourceIndex;
        public uint Background, Reserved;
        public double CornerRadius, ClickAmount, ClickX, ClickY;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Progress(uint completed, uint total);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Audio(long firstFrame, uint count, nint stereo);
    [DllImport(Dll, EntryPoint = "glide_render_audio_abi_version", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern uint AudioAbiVersion();
    [DllImport(Dll, EntryPoint = "glide_render_video_with_audio", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int RenderVideoWithAudio(string output, [In] Source[] sources, uint sourceCount, [In] Frame[] frames, uint frameCount,
        uint sw, uint sh, uint w, uint h, uint fps, long durationUs, Progress progress, Audio audio);
    [DllImport(Dll, EntryPoint = "glide_verify_audio", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VerifyAudio(string path, out ulong frames, out long durationUs);
    [DllImport(Dll, EntryPoint = "glide_render_abi_version", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern uint AbiVersion();
    [DllImport(Dll, EntryPoint = "glide_render_error_stage", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern uint ErrorStage();
    [DllImport(Dll, EntryPoint = "glide_preview_create", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PreviewCreate(uint sw, uint sh, uint w, uint h, out ulong id);
    [DllImport(Dll, EntryPoint = "glide_preview_frame", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PreviewFrame(ulong id, string source, in Frame frame, [Out] byte[] pixels, uint bytes);
    [DllImport(Dll, EntryPoint = "glide_preview_release", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void PreviewRelease(ulong id);
    [DllImport(Dll, EntryPoint = "glide_render_frame", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int RenderFrame(string source, in Frame frame, uint sw, uint sh, uint w, uint h, [Out] byte[] bgra, uint bytes);
    [DllImport(Dll, EntryPoint = "glide_render_video", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int RenderVideo(string output, [In] Source[] sources, uint sourceCount, [In] Frame[] frames, uint frameCount,
        uint sw, uint sh, uint w, uint h, uint fps, long durationUs, Progress progress);
    [DllImport(Dll, EntryPoint = "glide_verify_video", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int Verify(string path, out ulong frames, out long durationUs);
}
