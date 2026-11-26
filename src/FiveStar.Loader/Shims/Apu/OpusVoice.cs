// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Concentus;

namespace FiveStar.Loader.Shims.Apu;

/// <summary>
/// The decoder state of one XApu Opus stream. Opus packets depend on the packets before them, so a stream keeps one
/// decoder from its first packet to a reset; the decoder is made at the first decode, when the channel count is known.
/// Concentus's factory takes the native libopus beside the loader (package Concentus.Native.NetCore, <c>opus.dll</c>)
/// when it loads, else its C# port, which allocates its working arrays on every packet: about 200 MB of int arrays in
/// 20 s of w126's world (12% of the game threads' allocations), each collected by the GC.
/// </summary>
internal sealed class OpusVoice
{
    private const string NativeDecoderType = "NativeOpusDecoder";

    private static long _native;
    private static long _managed;

    private readonly float[] _scratch = new float[XApuLayout.MaxPacketSamples * XApuLayout.MaxChannels];
    private IOpusDecoder? _decoder;

    /// <summary>"N on native libopus, M on the C# port" over the decoders made so far.</summary>
    public static string Implementations => $"{Interlocked.Read(ref _native)} on native libopus, {Interlocked.Read(ref _managed)} on the C# port";

    /// <summary>Whether the stream's decoder, once made, is native libopus.</summary>
    internal bool Native => _decoder is not null && IsNative(_decoder);

    /// <summary>
    /// Decodes <paramref name="packet"/>, drops its first <paramref name="first"/> samples per channel and copies at most
    /// <paramref name="keep"/> samples per channel, interleaved, as fit into <paramref name="output"/>; returns the
    /// samples per channel written.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> output, int channels, int first, int keep)
    {
        if (_decoder is null || _decoder.NumChannels != channels)
        {
            _decoder = OpusCodecFactory.CreateDecoder(XApuLayout.OpusRate, channels);
            Count(_decoder);
        }

        int decoded = _decoder.Decode(packet, _scratch, XApuLayout.MaxPacketSamples, false);
        int skipped = Math.Min(first, decoded);
        int samples = Math.Min(Math.Min(decoded - skipped, keep), output.Length / channels);
        _scratch.AsSpan(skipped * channels, samples * channels).CopyTo(output);

        return samples;
    }

    /// <summary>Whether Concentus made its native adapter (an internal type, told by its name).</summary>
    private static bool IsNative(IOpusDecoder decoder)
    {
        return decoder.GetType().Name == NativeDecoderType;
    }

    private static void Count(IOpusDecoder decoder)
    {
        if (IsNative(decoder))
        {
            Interlocked.Increment(ref _native);

            return;
        }

        Interlocked.Increment(ref _managed);
    }

    public void Reset()
    {
        _decoder?.ResetState();
    }
}
