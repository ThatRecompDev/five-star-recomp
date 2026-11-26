// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// A capture on disk: <see cref="DispatchCapture.FileName"/> and the files it names, blobs under
/// <see cref="BlobFolder"/>. The loader writes it (the capture writer); <c>fivestar shader replay</c> reads it.
/// </summary>
public sealed class CaptureFolder
{
    /// <summary>The folder of the binary blobs.</summary>
    public const string BlobFolder = "blobs";

    private const string BlobExtension = ".bin";

    public CaptureFolder(string path)
    {
        Root = path;
    }

    public string Root { get; }

    /// <summary>Reads the manifest; one of another layout version is refused.</summary>
    public DispatchCapture Read()
    {
        using var stream = File.OpenRead(Path.Combine(Root, DispatchCapture.FileName));
        var capture = JsonSerializer.Deserialize(stream, CaptureJsonContext.Default.DispatchCapture)
            ?? throw new InvalidDataException($"{Root} holds no dispatch capture");

        if (capture.Version != DispatchCapture.CurrentVersion)
        {
            throw new InvalidDataException($"{Root}: capture version {capture.Version}, this build reads {DispatchCapture.CurrentVersion}");
        }

        return capture;
    }

    /// <summary>Writes the manifest last, so a folder with one is complete.</summary>
    public void Write(DispatchCapture capture)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, DispatchCapture.FileName);
        var temporary = path + ".tmp";

        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, capture, CaptureJsonContext.Default.DispatchCapture);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Writes blob <paramref name="name"/>; its path relative to the folder, as the manifest names it.</summary>
    public string WriteBlob(string name, ReadOnlySpan<byte> bytes)
    {
        return WriteFile(Path.Combine(BlobFolder, name + BlobExtension), bytes);
    }

    /// <summary>Writes a file at <paramref name="relative"/>; that path back.</summary>
    public string WriteFile(string relative, ReadOnlySpan<byte> bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        return relative.Replace('\\', '/');
    }

    public string WriteText(string relative, string text)
    {
        return WriteFile(relative, System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The bytes of a file the manifest names.</summary>
    public byte[] ReadFile(string relative)
    {
        return File.ReadAllBytes(PathOf(relative));
    }

    /// <summary>The full path of a file the manifest names.</summary>
    public string PathOf(string relative)
    {
        return Path.Combine(Root, relative);
    }
}
