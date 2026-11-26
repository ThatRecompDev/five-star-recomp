// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --typeless-depth: a depth texture in a typed depth format is made in its typeless family, so a shader view the game
/// places at its address reads its depth plane; without it the view is null and counted as such.
/// </summary>
public sealed unsafe class TypelessDepthTests
{
    private const int Size = 64;
    private const int D32Float = 40;
    private const int R32Float = 41;
    private const int Texture2D = 3;
    private const int SrvTexture2D = 4;
    private const int AllowDepthStencil = 0x2;
    private const int DefaultComponentMapping = 0x1688;
    private const int TextureMipLevelsWord = 5;
    private const int DepthWriteLayout = 4;
    private const ulong RangeBytes = 0x20000;
    private const uint Reserve64K = 0x20402000;
    private const uint GpuOnly = 0x801;

    [Fact]
    public void Typed_depth_formats_map_to_their_typeless_family_only_when_asked()
    {
        DepthFormats.TypelessDepth = false;
        Assert.Equal(D32Float, DepthFormats.TypelessResource(D32Float));

        try
        {
            DepthFormats.TypelessDepth = true;
            Assert.Equal(DepthFormats.R32Typeless, DepthFormats.TypelessResource(D32Float));
            Assert.Equal(DepthFormats.R24G8Typeless, DepthFormats.TypelessResource(DepthFormats.D24S8));
            Assert.Equal(DepthFormats.R16Typeless, DepthFormats.TypelessResource(DepthFormats.D16));
            Assert.Equal(R32Float, DepthFormats.TypelessResource(R32Float));

            Assert.True(DepthFormats.TryTypelessFamily(DepthFormats.R32Typeless, out int typed, out int shaderView));
            Assert.Equal((D32Float, R32Float), (typed, shaderView));
        }
        finally
        {
            DepthFormats.TypelessDepth = false;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_view_at_a_depth_texture_reads_it_once_the_texture_is_typeless(bool typeless)
    {
        DepthFormats.TypelessDepth = typeless;

        try
        {
            using var test = TestRuntime.Create();
            var device = new XboxDevice(test);
            var textures = test.Runtime.D3D12X.Textures;
            var allocate = (delegate* unmanaged<nint, nuint, uint, ulong, uint, nint>)test.Shim("xmem.dll", "XMemVirtualAlloc");
            nint address = allocate(0, (nuint)RangeBytes, Reserve64K, 0x3, GpuOnly);

            var description = new ResourceDescription
            {
                Dimension = Texture2D, Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1,
                Format = D32Float, SampleCount = 1, Flags = AllowDepthStencil,
            };
            description.Layout = (int)(XgSwizzleModes.ModeBase | XgSwizzleModes.Suggest(Size * Size * sizeof(float)));
            Place(device, address, description);

            using var probe = new TextureProbe(test.Runtime.D3D12X.Objects.DesktopDevice);
            View(device, address, description, (nint)probe.Heap.GetCPUDescriptorHandleForHeapStart().Ptr);

            if (typeless)
            {
                Assert.Equal(0, textures.Counters[GpuResourceEvent.ViewNull]);
                Assert.Equal(1, textures.Counters[GpuResourceEvent.ViewDepthResolved]);
                Assert.Equal(DepthFormats.R32Typeless, textures.Registry.Find((ulong)address)!.DesktopFormat);
            }
            else
            {
                Assert.Equal(1, textures.Counters[GpuResourceEvent.ViewNull]);
                Assert.Equal(1, textures.Counters[GpuResourceEvent.ViewNullDepth]);
            }
        }
        finally
        {
            DepthFormats.TypelessDepth = false;
        }
    }

    private static void Place(XboxDevice device, nint address, ResourceDescription description)
    {
        var iid = new Guid("696442be-a72e-4059-bc79-5b5c98040fad");
        nint resource = 0;
        var create = (delegate* unmanaged<nint, nint, ResourceDescription*, uint, void*, Guid*, nint*, int>)XboxDevice.Vtable(device.Device)[133];
        Assert.Equal(0, create(device.Device, address, &description, DepthWriteLayout, null, &iid, &resource));
    }

    /// <summary>Device slot 70 with an R32_FLOAT Texture2D view of the depth texture at <paramref name="address"/>.</summary>
    private static void View(XboxDevice device, nint address, ResourceDescription description, nint handle)
    {
        var view = new AddressViewDescription { Format = R32Float, ViewDimension = SrvTexture2D, Address = (ulong)address };
        var words = (int*)&view;
        words[2] = DefaultComponentMapping;
        words[TextureMipLevelsWord] = 1;
        var create = (delegate* unmanaged<nint, ResourceDescription*, AddressViewDescription*, nint, void>)XboxDevice.Vtable(device.Device)[70];
        create(device.Device, &description, &view, handle);
    }
}
