// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>Per-channel minimum, maximum, mean and NaN count of one readback, as <see cref="StagePixels"/> logs them.</summary>
internal sealed class ChannelStats
{
    private const string ChannelNames = "rgba";

    private readonly double[] _sums;
    private readonly long[] _counts;
    private readonly long[] _nans;
    private readonly float[] _min;
    private readonly float[] _max;

    public ChannelStats(int channels)
    {
        _sums = new double[channels];
        _counts = new long[channels];
        _nans = new long[channels];
        _min = new float[channels];
        _max = new float[channels];

        Array.Fill(_min, float.MaxValue);
        Array.Fill(_max, float.MinValue);
    }

    /// <summary>The smallest value above 0.0 of channel 0 (the nearest reverse-Z depth); float.MaxValue when none.</summary>
    public float SmallestPositive { get; private set; } = float.MaxValue;

    public void Add(int channel, float value)
    {
        if (float.IsNaN(value))
        {
            _nans[channel]++;

            return;
        }

        if (float.IsInfinity(value))
        {
            return;
        }

        _sums[channel] += value;
        _counts[channel]++;
        _min[channel] = Math.Min(_min[channel], value);
        _max[channel] = Math.Max(_max[channel], value);

        if (channel == 0 && value > 0)
        {
            SmallestPositive = Math.Min(SmallestPositive, value);
        }
    }

    public float Largest(int channel)
    {
        return _max[channel];
    }

    /// <summary>Whether the colour channels hold a value below -<paramref name="slack"/> and none outside -1..1 by more than it.</summary>
    public bool Signed(float slack)
    {
        bool negative = false;

        for (int channel = 0; channel < _min.Length; channel++)
        {
            if (_counts[channel] == 0)
            {
                continue;
            }

            if (_min[channel] < -1f - slack || _max[channel] > 1f + slack)
            {
                return false;
            }

            negative |= _min[channel] < -slack;
        }

        return negative;
    }

    /// <summary>"r min/max/mean a/b/c, NaN n; g ...".</summary>
    public string Describe()
    {
        var parts = new List<string>();

        for (int channel = 0; channel < _min.Length; channel++)
        {
            if (_counts[channel] == 0)
            {
                parts.Add($"{ChannelNames[channel]} no finite values, NaN {_nans[channel]}");

                continue;
            }

            double mean = _sums[channel] / _counts[channel];
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ChannelNames[channel]} min/max/mean {_min[channel]:G4}/{_max[channel]:G4}/{mean:G4}, NaN {_nans[channel]}"));
        }

        return string.Join("; ", parts);
    }
}
