using System.Buffers.Binary;
using Versio.Core.Tests;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The application icon is a build input, and a silently malformed one is a bad failure:
/// the build succeeds, the app runs, and the only symptom is a blank square in the taskbar
/// on someone else's machine. These tests read the shipped <c>assets/versio.ico</c> and
/// check the properties that actually break shells.
/// </summary>
/// <remarks>
/// This lives in the integration suite rather than the unit suite because it reads a file
/// from the working tree. It needs no CODESYS and no git, so unlike its neighbours here it
/// never skips.
/// </remarks>
public class ApplicationIconTests
{
    /// <summary>Sizes Windows requests. 24 is included because Alt-Tab asks for it.</summary>
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static byte[] ReadIcon() =>
        File.ReadAllBytes(Path.Combine(TestPaths.Root, "assets", "versio.ico"));

    /// <summary>One decoded ICONDIRENTRY.</summary>
    private sealed record IconEntry(int Size, int Offset, int Length, int BitCount);

    private static List<IconEntry> ReadDirectory(byte[] ico)
    {
        // ICONDIR: reserved(2) = 0, type(2) = 1, count(2).
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2, 2)));

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        var entries = new List<IconEntry>(count);

        for (int i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + (16 * i), 16);

            // Width and height are single bytes, so 256 is encoded as 0. Decoding this
            // wrongly is the classic way to produce an icon Windows shows at 1x1.
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            Assert.Equal(width, height);

            entries.Add(new IconEntry(
                Size: width,
                Offset: BinaryPrimitives.ReadInt32LittleEndian(entry[12..16]),
                Length: BinaryPrimitives.ReadInt32LittleEndian(entry[8..12]),
                BitCount: BinaryPrimitives.ReadUInt16LittleEndian(entry[6..8])));
        }

        return entries;
    }

    [Fact]
    public void The_icon_exists_and_is_a_valid_icon_container()
    {
        var entries = ReadDirectory(ReadIcon());

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Every_size_Windows_asks_for_is_present()
    {
        // Missing sizes do not fail; Windows resamples the nearest one, and the result is
        // the mush this icon was hand-tuned per size to avoid.
        var sizes = ReadDirectory(ReadIcon()).Select(e => e.Size).ToHashSet();

        Assert.All(RequiredSizes, size =>
            Assert.True(sizes.Contains(size), $"the .ico has no {size} px entry"));
    }

    [Fact]
    public void No_size_appears_twice()
    {
        // A duplicate entry makes the chosen image depend on reader order - the icon then
        // differs between Explorer and the taskbar for no visible reason.
        var sizes = ReadDirectory(ReadIcon()).Select(e => e.Size).ToList();

        Assert.Equal(sizes.Count, sizes.Distinct().Count());
    }

    [Fact]
    public void Every_entry_is_32_bit_so_the_corners_stay_transparent()
    {
        // At 8-bit the rounded tile corners come out as a hard rectangle against whatever
        // the shell background happens to be.
        var entries = ReadDirectory(ReadIcon());

        Assert.All(entries, e => Assert.Equal(32, e.BitCount));
    }

    [Fact]
    public void Every_entry_points_at_data_inside_the_file()
    {
        // An offset past the end is how a truncated write manifests, and several shells
        // render it as a blank square rather than reporting an error.
        var ico = ReadIcon();

        Assert.All(ReadDirectory(ico), e =>
        {
            Assert.True(e.Offset > 0, "entry offset must follow the directory");
            Assert.True(e.Offset + e.Length <= ico.Length,
                $"the {e.Size} px entry runs {e.Offset + e.Length - ico.Length} bytes past the end of the file");
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    public void Small_entries_are_DIBs(int size)
    {
        // Windows 10 and 11 read PNG at any size, but some older shell surfaces and
        // installer UIs still expect DIBs for the small entries. tools/New-VersioIcon.ps1
        // writes both formats deliberately; this pins that choice so a "simplification"
        // to PNG-everywhere is a deliberate act rather than an accident.
        var ico = ReadIcon();
        var entry = ReadDirectory(ico).Single(e => e.Size == size);

        Assert.False(ico.AsSpan(entry.Offset, 4).SequenceEqual(PngSignature),
            $"the {size} px entry is a PNG; small entries are meant to be DIBs");

        // BITMAPINFOHEADER: biSize is 40, and biHeight is doubled to cover the AND mask.
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry.Offset, 4)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry.Offset + 4, 4)));
        Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry.Offset + 8, 4)));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void Large_entries_are_PNGs(int size)
    {
        // A 256 px entry as an uncompressed DIB costs about 256 KB and inflates every
        // installer and shortcut that carries the icon.
        var ico = ReadIcon();
        var entry = ReadDirectory(ico).Single(e => e.Size == size);

        Assert.True(ico.AsSpan(entry.Offset, 4).SequenceEqual(PngSignature),
            $"the {size} px entry is not a PNG");
    }

    [Fact]
    public void The_whole_file_stays_small_enough_to_embed_everywhere()
    {
        // Shortcuts, jump lists and the installer all carry a copy. 100 KB is generous for
        // a flat mark; blowing past it means something is being stored uncompressed.
        var length = ReadIcon().Length;

        Assert.True(length < 100 * 1024, $"versio.ico is {length / 1024} KB");
    }

    [Fact]
    public void The_project_actually_references_the_icon()
    {
        // Everything above can pass while the app still ships with the default blank icon,
        // because nothing else in the build fails when ApplicationIcon is absent.
        var csproj = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Versio.App", "Versio.App.csproj"));

        Assert.Contains("<ApplicationIcon>", csproj, StringComparison.Ordinal);
        Assert.Contains("versio.ico", csproj, StringComparison.Ordinal);
    }
}
