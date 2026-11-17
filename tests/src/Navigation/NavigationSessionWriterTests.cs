using System.IO.Compression;
using System.Text.Json;
using AlleyCat.Navigation.Recording;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Bounded recorder durability and loss contracts, without engine or native inference.</summary>
public class NavigationSessionWriterTests
{
    private static string NewPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../game/temp/motionbricks/navigation-recorder-01/unit", Guid.NewGuid().ToString("N")));

    /// <inheritdoc/>
    /// <summary>A saved prefix stays readable while later owned values reach finalisation.</summary>
    [Fact]
    public async Task CheckpointContinuesAndFinalPayloadIsAnOwnedLosslessCopy()
    {
        string path = NewPath();
        var writer = new NavigationSessionWriter(path, new
        {
            test = "transport"
        });
        float[] values = [1.125f, -2.75f, float.Epsilon];
        Assert.True(writer.Record("returned", new
        {
            ticket = 7
        }, values.Length, () => (float[])values.Clone()));
        values[0] = 999;
        Assert.True(writer.Record("provider-error", new
        {
            error = "synthetic no-motion result"
        }, 0, () => []));
        Assert.True(await writer.CheckpointAsync());
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json"))))
        {
            Assert.Equal("recording", manifest.RootElement.GetProperty("state").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("savedThroughSequence").GetInt64());
        }
        Assert.True(writer.Record("bookmark", new
        {
            attempt = 2
        }));
        Assert.True(await writer.FinishAsync());
        await writer.Completion;
        using var final = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        Assert.Equal("completed", final.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, final.RootElement.GetProperty("segments").GetArrayLength());
        Assert.Empty(Directory.GetFiles(path, "*.partial"));
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(path, "motion-0001.bin.gz")), CompressionMode.Decompress);
        using var binary = new BinaryReader(gzip);
        Assert.Equal("ACNAV001"u8.ToArray(), binary.ReadBytes(8));
        Assert.Equal(1, binary.ReadInt32());
        Assert.Equal(1, binary.ReadInt64());
        Assert.Equal(3, binary.ReadInt32());
        Assert.Equal(1.125f, binary.ReadSingle());
        Assert.Equal(-2.75f, binary.ReadSingle());
        Assert.Equal(float.Epsilon, binary.ReadSingle());
        Assert.Equal(-1, gzip.ReadByte());
    }

    /// <inheritdoc/>
    /// <summary>Oversize and unavailable storage are explicit without throwing into gameplay.</summary>
    [Fact]
    public async Task OversizeIsExplicitAndAnUnavailableDirectoryDoesNotThrowIntoProducer()
    {
        string path = NewPath();
        var bounded = new NavigationSessionWriter(path, new
        {
        }, byteLimit: 256, capacity: 1);
        Assert.False(bounded.Record("oversize", new
        {
        }, 1000, () => throw new Exception("Must not copy rejected payload")));
        Assert.True(await bounded.FinishAsync());
        Assert.Equal(1, bounded.Dropped);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        Assert.Equal("lossy", manifest.RootElement.GetProperty("integrity").GetString());
        string unavailable = NewPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(unavailable)!);
        File.WriteAllText(unavailable, "file, not directory");
        var failed = new NavigationSessionWriter(unavailable, new
        {
        });
        await failed.Completion;
        Assert.NotNull(failed.Error);
        Assert.False(failed.Record("still-playing", new
        {
        }));
        Assert.False(await failed.FinishAsync());
    }
}
