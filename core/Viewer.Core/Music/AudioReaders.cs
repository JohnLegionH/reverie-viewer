// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Music;

/// <summary>A decoder of one stream: interleaved float samples in the range -1 to 1.</summary>
internal interface IPcmReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Reads up to <paramref name="count"/> samples (all channels together) into <paramref name="buffer"/>; 0 at the end of the stream.</summary>
    int Read(float[] buffer, int count);
}

internal static class AudioReaders
{
    /// <summary>The decoder for a format. The decoders are NLayer (MP3), NVorbis (Ogg Vorbis) and SharpJaad (AAC), all managed and MIT licensed.</summary>
    public static IPcmReader Open(AudioFormat format, Stream stream) => format switch
    {
        AudioFormat.Mp3 => new Mp3Reader(stream),
        AudioFormat.OggVorbis => new VorbisStreamReader(stream),
        AudioFormat.Aac => new AacReader(stream),
        _ => throw new NotSupportedException("this is not an audio format the player reads"),
    };
}

/// <summary>MP3 (MPEG-1, 2 and 2.5, layers I to III) through NLayer.</summary>
internal sealed class Mp3Reader : IPcmReader
{
    private readonly NLayer.MpegFile _file;

    public Mp3Reader(Stream stream) => _file = new NLayer.MpegFile(stream);

    public int SampleRate => _file.SampleRate;
    public int Channels => _file.Channels;
    public int Read(float[] buffer, int count) => _file.ReadSamples(buffer, 0, count);
    public void Dispose() => _file.Dispose();
}

/// <summary>Ogg Vorbis through NVorbis. A station that changes track starts a new Vorbis stream inside the same Ogg stream; the reader moves on to it.</summary>
internal sealed class VorbisStreamReader : IPcmReader
{
    private readonly NVorbis.VorbisReader _reader;

    public VorbisStreamReader(Stream stream) => _reader = new NVorbis.VorbisReader(stream, closeOnDispose: false);

    public int SampleRate => _reader.SampleRate;
    public int Channels => _reader.Channels;

    public int Read(float[] buffer, int count)
    {
        int n = _reader.ReadSamples(buffer, 0, count);
        if (n == 0 && _reader.FindNextStream())
            n = _reader.ReadSamples(buffer, 0, count);
        return n;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>AAC in ADTS frames (what Icecast and Shoutcast stations send) through SharpJaad: AAC-LC, HE-AAC and the older profiles it decodes.</summary>
internal sealed class AacReader : IPcmReader
{
    private readonly Stream _stream;
    private readonly byte[] _input = new byte[16 * 1024];
    private int _have;
    private SharpJaad.AAC.Decoder? _decoder;
    private (int Profile, int Rate, int Channels) _config;
    private readonly SharpJaad.AAC.SampleBuffer _out = new();
    private byte[] _pcm = [];
    private int _pcmAt, _pcmEnd;

    public AacReader(Stream stream)
    {
        _stream = stream;
        _out.SetBigEndian(false);
        Fill();   // the first frame tells the rate and channel count
        Decode();
        if (_decoder is null)
            throw new InvalidDataException("no AAC frame found at the start of the stream");
    }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public int Read(float[] buffer, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pcmAt >= _pcmEnd && !Decode())
                break;
            while (written < count && _pcmAt + 1 < _pcmEnd)
            {
                buffer[written++] = (short)(_pcm[_pcmAt] | _pcm[_pcmAt + 1] << 8) / 32768f;
                _pcmAt += 2;
            }
        }
        return written;
    }

    /// <summary>Reads the next frame and decodes it into the pending samples; false at the end of the stream.</summary>
    private bool Decode()
    {
        while (true)
        {
            // find a frame header: 12 sync bits, layer 00
            int at = 0;
            while (at + 7 <= _have && !(_input[at] == 0xFF && (_input[at + 1] & 0xF6) == 0xF0))
                at++;
            if (at > 0)
                Drop(at);
            if (_have >= 7)
            {
                int length = ((_input[3] & 3) << 11) | (_input[4] << 3) | (_input[5] >> 5);
                int header = (_input[1] & 1) == 0 ? 9 : 7;
                int sampleIndex = (_input[2] >> 2) & 0xF;
                if (length < header || length > _input.Length || sampleIndex >= 12)
                {
                    Drop(1);   // not a real header: look further on
                    continue;
                }
                if (_have >= length)
                {
                    int profile = (_input[2] >> 6) + 1, channels = ((_input[2] & 1) << 2) | (_input[3] >> 6);
                    var frame = new byte[length - header];
                    Array.Copy(_input, header, frame, 0, frame.Length);
                    Drop(length);
                    if (channels == 0)
                        continue;   // a channel layout set inside the frame: not used by radio streams
                    if (_decoder is null || _config != (profile, sampleIndex, channels))
                    {
                        var config = new SharpJaad.AAC.DecoderConfig();
                        config.SetProfile((SharpJaad.AAC.Profile)profile);
                        config.SetSampleFrequency((SharpJaad.AAC.SampleFrequency)sampleIndex);
                        config.SetChannelConfiguration((SharpJaad.AAC.ChannelConfiguration)channels);
                        _decoder = new SharpJaad.AAC.Decoder(config);
                        _config = (profile, sampleIndex, channels);
                    }
                    try { _decoder.DecodeFrame(frame, _out); }
                    catch (Exception e) when (e is not OutOfMemoryException) { continue; }   // a damaged frame is skipped, as a player does
                    var data = _out.Data;
                    if (data is null || data.Length == 0)
                        continue;
                    SampleRate = _out.SampleRate;
                    Channels = _out.Channels;
                    if (_pcm.Length < data.Length)
                        _pcm = new byte[data.Length];
                    Array.Copy(data, _pcm, data.Length);
                    _pcmAt = 0;
                    _pcmEnd = data.Length;
                    return true;
                }
            }
            if (!Fill())
                return false;
        }
    }

    private bool Fill()
    {
        if (_have == _input.Length)
            return true;
        int n = _stream.Read(_input, _have, _input.Length - _have);
        _have += n;
        return n > 0;
    }

    private void Drop(int n)
    {
        Array.Copy(_input, n, _input, 0, _have - n);
        _have -= n;
    }

    public void Dispose() { }
}
