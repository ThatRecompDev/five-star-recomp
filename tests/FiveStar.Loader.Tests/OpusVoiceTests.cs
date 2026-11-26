// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Concentus;
using Concentus.Enums;
using FiveStar.Loader.Shims.Apu;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The XApu Opus voice on Windows x64 decodes with the native libopus the loader ships (Concentus.Native.NetCore), not
/// Concentus's C# port, whose per-packet arrays were 12% of the game threads' allocations in w126.
/// </summary>
public sealed class OpusVoiceTests
{
    private const int Frame = 960;
    private const int PacketBytes = 1275;
    private const int Frames = 10;
    private const float Amplitude = 0.5f;
    private const float Tone = 440;

    [Fact]
    public void A_voice_decodes_with_native_libopus_where_the_loader_ships_it()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return;
        }

        var encoder = OpusCodecFactory.CreateEncoder(XApuLayout.OpusRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        var voice = new OpusVoice();
        var pcm = new float[Frame];
        var packet = new byte[PacketBytes];
        var output = new float[Frame];
        int decoded = 0;

        for (int frame = 0; frame < Frames; frame++)
        {
            for (int sample = 0; sample < Frame; sample++)
            {
                pcm[sample] = Amplitude * MathF.Sin(2 * MathF.PI * Tone * ((frame * Frame) + sample) / XApuLayout.OpusRate);
            }

            int length = encoder.Encode(pcm, Frame, packet, packet.Length);
            decoded += voice.Decode(packet.AsSpan(0, length), output, 1, 0, Frame);
        }

        Assert.True(voice.Native, OpusVoice.Implementations);
        Assert.Equal(Frames * Frame, decoded);
        Assert.InRange(Math.Sqrt(output.Average(value => (double)value * value)), 0.3, 0.4);
    }
}
