// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>What happened to a texture or view at a console address; counted by <see cref="GpuResourceCounters"/>.</summary>
internal enum GpuResourceEvent
{
    /// <summary>Slot 133 made a texture.</summary>
    PlacedTexture,

    /// <summary>Slot 70 was called.</summary>
    View,

    /// <summary>A slot-70 view found a registered texture.</summary>
    ViewResolved,

    /// <summary>A slot-70 view made a texture from the memory at its address.</summary>
    ViewCreatedTexture,

    /// <summary>A slot-70 view describes a buffer (left a null descriptor).</summary>
    ViewBuffer,

    /// <summary>A slot-70 view got a null descriptor (no readable memory, a depth texture or a failed creation).</summary>
    ViewNull,

    /// <summary>
    /// A slot-70 view of a full-format texture asked for another member of its cast family (UNORM_SRGB on UNORM, UINT on
    /// UNORM or FLOAT) and reads in the texture's format: the sRGB decode or the integer reading is lost.
    /// </summary>
    ViewCastLost,

    /// <summary>A slot-70 view of a GPU-written texture asked for a format outside its family (an alias of its bytes) and reads in the texture's.</summary>
    ViewReinterpretLost,

    /// <summary>A texture was filled from memory.</summary>
    Upload,

    /// <summary>A refresh found the memory unchanged.</summary>
    UploadSkipped,

    /// <summary>A refresh was not done: a GPU copy wrote the texture, and its memory does not hold that.</summary>
    UploadKeptCopy,

    /// <summary>A fill failed: the desktop refused its upload buffer or queue, or the copy did not finish.</summary>
    UploadFailed,

    /// <summary>A texture could not be placed in a shared heap (<c>--placed-textures</c>) and was committed.</summary>
    PlacedFailed,

    /// <summary>A fill was refused: the console layout does not fit the desktop texture's subresources (left unfilled).</summary>
    UploadRefused,

    /// <summary>A refresh found no page of the texture's memory mapped (or memory outside the XMem reservations not readable): left as it was.</summary>
    UploadUnreadable,

    /// <summary>A refresh read a texture in an XMem reservation page by page, zeros for the pages mapped nowhere.</summary>
    UploadGathered,

    /// <summary>A slot-70 view made a texture over reserved memory no page of which is mapped yet (filled when pages map).</summary>
    ViewCreatedUnmapped,

    /// <summary>A slot-70 view over reserved memory with no page mapped got a null descriptor: such textures reached <see cref="GpuTextures.UnmappedViewBudget"/>.</summary>
    ViewUnmappedOverBudget,

    /// <summary>A view of a texture retired over unmapped memory, no record naming it, became a null view.</summary>
    ViewNulled,

    /// <summary>A DirectStorage write or a new page mapping changed memory under a texture filled from memory.</summary>
    Written,

    /// <summary>A texture so changed was refreshed before the next submission.</summary>
    WrittenRefreshed,

    /// <summary>A written texture uploaded only the subresources its changed pages lie in (<see cref="TextureRefreshWorker"/>).</summary>
    UploadPartial,

    /// <summary>A slot-70 view queued its texture's page check on the refresh worker.</summary>
    ViewCheckQueued,

    /// <summary>A slot-70 view of a texture checked already this frame: nothing to do.</summary>
    ViewCheckSkipped,
}
