using Android.Media;
using CuePilotDesktop.Services;

namespace CuePilot.Android.Platform;

/// <summary>
/// Sound track of an audio or video file through Android MediaExtractor + MediaCodec (mp3, m4a, aac, ogg, opus,
/// flac, amr, wav, mp4, 3gp, webm… whatever the phone can play), as 16 kHz mono for the speech API.
/// </summary>
public sealed class AndroidMediaDecoder : IMediaPcmReader
{
    private const long TimeoutUs = 10_000;
    private readonly MediaExtractor _extractor = new();
    private readonly MediaCodec _codec;
    private readonly PcmResampler _resampler = new();
    private readonly MediaCodec.BufferInfo _info = new();
    private bool _inputDone;
    private bool _outputDone;
    private bool _floatPcm;

    public AndroidMediaDecoder(string path)
    {
        _extractor.SetDataSource(path);
        MediaFormat? format = null;
        for (int i = 0; i < _extractor.TrackCount; i++)
        {
            MediaFormat candidate = _extractor.GetTrackFormat(i);
            if (candidate.GetString(MediaFormat.KeyMime)?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            {
                _extractor.SelectTrack(i);
                format = candidate;
                break;
            }
        }

        if (format == null)
        {
            _extractor.Release();
            throw new InvalidOperationException("The file has no sound track.");
        }

        string mime = format.GetString(MediaFormat.KeyMime)!;
        Duration = format.ContainsKey(MediaFormat.KeyDuration)
            ? TimeSpan.FromTicks(format.GetLong(MediaFormat.KeyDuration) * 10)
            : TimeSpan.Zero;
        _resampler.SetFormat(format.GetInteger(MediaFormat.KeySampleRate), format.GetInteger(MediaFormat.KeyChannelCount));
        _codec = MediaCodec.CreateDecoderByType(mime);
        _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
        _codec.Start();
    }

    public TimeSpan Duration { get; }

    public int Read(short[] buffer)
    {
        while (_resampler.Available < buffer.Length && !_outputDone)
        {
            Pump();
        }

        return _resampler.Take(buffer);
    }

    private void Pump()
    {
        if (!_inputDone)
        {
            int inputIndex = _codec.DequeueInputBuffer(TimeoutUs);
            if (inputIndex >= 0)
            {
                Java.Nio.ByteBuffer input = _codec.GetInputBuffer(inputIndex)!;
                int size = _extractor.ReadSampleData(input, 0);
                if (size < 0)
                {
                    _codec.QueueInputBuffer(inputIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                    _inputDone = true;
                }
                else
                {
                    _codec.QueueInputBuffer(inputIndex, 0, size, _extractor.SampleTime, MediaCodecBufferFlags.None);
                    _extractor.Advance();
                }
            }
        }

        int outputIndex = _codec.DequeueOutputBuffer(_info, TimeoutUs);
        if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            MediaFormat format = _codec.OutputFormat;
            _resampler.SetFormat(format.GetInteger(MediaFormat.KeySampleRate), format.GetInteger(MediaFormat.KeyChannelCount));
            _floatPcm = format.ContainsKey(MediaFormat.KeyPcmEncoding) && format.GetInteger(MediaFormat.KeyPcmEncoding) == (int)Encoding.PcmFloat;
            return;
        }

        if (outputIndex < 0)
        {
            return;
        }

        if (_info.Size > 0)
        {
            Java.Nio.ByteBuffer output = _codec.GetOutputBuffer(outputIndex)!;
            output.Position(_info.Offset);
            output.Limit(_info.Offset + _info.Size);
            var bytes = new byte[_info.Size];
            output.Get(bytes);
            _resampler.Add(_floatPcm ? FromFloat(bytes) : FromPcm16(bytes));
        }

        _codec.ReleaseOutputBuffer(outputIndex, false);
        if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
        {
            _outputDone = true;
        }
    }

    private static short[] FromPcm16(byte[] bytes)
    {
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static short[] FromFloat(byte[] bytes)
    {
        var samples = new short[bytes.Length / 4];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(BitConverter.ToSingle(bytes, i * 4) * 32767, short.MinValue, short.MaxValue);
        }

        return samples;
    }

    public void Dispose()
    {
        try
        {
            _codec.Stop();
        }
        catch (Exception)
        {
        }

        _codec.Release();
        _extractor.Release();
    }
}
