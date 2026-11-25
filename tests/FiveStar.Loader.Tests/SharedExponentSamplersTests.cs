// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Binding;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The programs a boot sees sampling shared-exponent light are listed for the next boot.</summary>
public sealed unsafe class SharedExponentSamplersTests
{
    [Fact]
    public void A_program_sampling_a_class_3_record_is_listed_once_and_the_next_boot_reads_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fivestar-se-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            SharedExponentSamplers.Load(folder);
            SharedExponentSamplers.NoteView();
            const nint Pipeline = 0x5E5E;
            SharedExponentSamplers.Keep(Pipeline, [new SampledRecord("se_test_light", 2, 0x40), new SampledRecord("se_test_fields", 2, 0x80)]);

            // The table in user SGPR 2 starts at window byte 0x100: its record +0x40 is class 3, +0x80 a uint view.
            var userData = new uint[64];
            userData[2] = 0x100;
            var window = new uint[0x100];
            window[(0x100 + 0x40) / 4 + 3] = 3;
            window[(0x100 + 0x80) / 4 + 3] = 1;

            fixed (uint* data = userData)
            fixed (uint* records = window)
            {
                SharedExponentSamplers.Check(Pipeline, data, (byte*)records);
                SharedExponentSamplers.Check(Pipeline, data, (byte*)records);
            }

            Assert.Equal(["se_test_light"], File.ReadAllLines(Path.Combine(folder, SharedExponentSamplers.FileName)));
            Assert.Equal(["se_test_light"], SharedExponentSamplers.Load(folder));
        }
        finally
        {
            SharedExponentSamplers.Folder = null;
            Directory.Delete(folder, recursive: true);
        }
    }
}
