using System.Runtime.InteropServices.WindowsRuntime;
using Glide.Core;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Glide.App;

// The MediaPlayer audio clock drives editor video selection. Sample requests use
// the same immutable mixer as export, with bounded buffers and verified leases.
internal sealed class AudioPreviewSource : IDisposable
{
    private readonly object gate = new();
    private readonly AudioMixer mixer;
    private readonly MediaStreamSource stream;
    private long nextFrame;
    private bool firstStart = true, disposed;
    private Exception? failure;
    internal Exception? Failure => Volatile.Read(ref failure);
    internal MediaSource Source { get; }
    internal AudioPreviewSource(ProjectStore store, ProjectDocument project, long startUs)
    {
        mixer = new(store, project);
        try
        {
            nextFrame = Math.Clamp(startUs * 48_000 / 1_000_000, 0, mixer.FrameCount - 1);
            stream = new(new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48_000, 2, 16)))
            { CanSeek = true, Duration = TimeSpan.FromMicroseconds(mixer.DurationUs), BufferTime = TimeSpan.FromMilliseconds(300) };
            stream.Starting += Starting; stream.SampleRequested += SampleRequested;
            Source = MediaSource.CreateFromMediaStreamSource(stream);
        }
        catch { mixer.Dispose(); throw; }
    }
    private void Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (!firstStart && args.Request.StartPosition is TimeSpan desired)
                    nextFrame = Math.Min(Math.Clamp(desired.Ticks, 0, mixer.DurationUs * 10) * 48_000 / 10_000_000, mixer.FrameCount - 1);
                firstStart = false;
                args.Request.SetActualStartPosition(TimeSpan.FromTicks(nextFrame * 10_000_000 / 48_000));
            }
            catch (Exception error) { Fail(sender, error); }
        }
    }
    private void SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (gate)
        {
            if (disposed || nextFrame >= mixer.FrameCount) return;
            try
            {
                int count = (int)Math.Min(4800, mixer.FrameCount - nextFrame);
                short[] pcm = new short[count * 2]; mixer.Read(nextFrame, pcm);
                byte[] bytes = new byte[count * 4]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                long ticks = nextFrame * 10_000_000 / 48_000;
                var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), TimeSpan.FromTicks(ticks));
                nextFrame += count;
                sample.Duration = TimeSpan.FromTicks(nextFrame * 10_000_000 / 48_000 - ticks);
                args.Request.Sample = sample;
            }
            catch (Exception error) { Fail(sender, error); }
        }
    }
    private void Fail(MediaStreamSource sender, Exception error)
    {
        Volatile.Write(ref failure, error);
        // The player can close its source while a request is finishing. Preserve
        // the original error without throwing back through a native event callback.
        try { sender.NotifyError(MediaStreamSourceErrorStatus.Other); } catch (Exception) { }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            stream.Starting -= Starting; stream.SampleRequested -= SampleRequested;
        }
        try { Source.Dispose(); } finally { mixer.Dispose(); }
    }
}
