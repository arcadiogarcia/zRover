using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using zRover.Retriever.Packages;

namespace zRover.Retriever.Tests;

/// <summary>
/// Tests for <see cref="MsixPacker"/>, the pure-.NET replacement for
/// <c>makeappx.exe pack</c> from the Windows SDK.
/// All tests use temp files and in-process data — no external tools required.
/// </summary>
public sealed class MsixPackerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"MsixPackerTests-{Guid.NewGuid():N}");

    public MsixPackerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ReadPublisher ────────────────────────────────────────────────────────

    [Fact]
    public void ReadPublisher_ReturnsCorrectPublisher()
    {
        var msix = CreateMsix(publisher: "CN=TestPublisher");
        MsixPacker.ReadPublisher(msix).Should().Be("CN=TestPublisher");
    }

    [Fact]
    public void ReadPublisher_ThrowsWhenManifestMissing()
    {
        var msix = TempMsix();
        using (var archive = ZipFile.Open(msix, ZipArchiveMode.Create))
            AddTextEntry(archive, "[Content_Types].xml", MinimalContentTypes());

        var act = () => MsixPacker.ReadPublisher(msix);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AppxManifest.xml*");
    }

    [Fact]
    public void ReadPublisher_ThrowsWhenPublisherAttributeMissing()
    {
        var msix = TempMsix();
        using (var archive = ZipFile.Open(msix, ZipArchiveMode.Create))
        {
            AddTextEntry(archive, "[Content_Types].xml", MinimalContentTypes());
            // Manifest without Publisher attribute
            AddTextEntry(archive, "AppxManifest.xml",
                """<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="App"/></Package>""");
        }

        var act = () => MsixPacker.ReadPublisher(msix);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Publisher*");
    }

    // ── RepackWithPatchedPublisherAsync ──────────────────────────────────────

    [Fact]
    public async Task RepackWithPatchedPublisher_ManifestPublisherIsUpdated()
    {
        var msix = CreateMsix(publisher: "CN=OldPublisher");
        await MsixPacker.RepackWithPatchedPublisherAsync(msix, "CN=NewPublisher");
        MsixPacker.ReadPublisher(msix).Should().Be("CN=NewPublisher");
    }

    [Fact]
    public async Task RepackWithPatchedPublisher_SignatureIsRemoved()
    {
        var msix = CreateMsix(publisher: "CN=OldPublisher", includeSignature: true);
        await MsixPacker.RepackWithPatchedPublisherAsync(msix, "CN=NewPublisher");

        using var zip = ZipFile.OpenRead(msix);
        zip.GetEntry("AppxSignature.p7x").Should().BeNull("old signature must be stripped");
    }

    [Fact]
    public async Task RepackWithPatchedPublisher_BlockMapIsRegenerated()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var msix    = CreateMsix(publisher: "CN=OldPublisher",
                                 payloadFiles: new() { ["payload.bin"] = payload });

        await MsixPacker.RepackWithPatchedPublisherAsync(msix, "CN=NewPublisher");

        using var zip   = ZipFile.OpenRead(msix);
        var bmEntry = zip.GetEntry("AppxBlockMap.xml");
        bmEntry.Should().NotBeNull();

        using var stream   = bmEntry!.Open();
        var doc       = XDocument.Load(stream);
        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";

        // Block map must contain an entry for the payload file
        doc.Root!.Elements(ns + "File")
            .Should().ContainSingle(f => f.Attribute("Name")!.Value == "payload.bin");
    }

    [Fact]
    public async Task RepackWithPatchedPublisher_PayloadFileBytesPreserved()
    {
        var payload = Encoding.UTF8.GetBytes("Hello, World!");
        var msix    = CreateMsix(publisher: "CN=OldPublisher",
                                 payloadFiles: new() { ["data/hello.txt"] = payload });

        await MsixPacker.RepackWithPatchedPublisherAsync(msix, "CN=NewPublisher");

        using var zip = ZipFile.OpenRead(msix);
        var entry = zip.GetEntry("data/hello.txt");
        entry.Should().NotBeNull();

        using var stream = entry!.Open();
        using var ms     = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    // ── BuildBlockMap ────────────────────────────────────────────────────────

    [Fact]
    public void BuildBlockMap_SingleFileSingleBlock_HasOneBlockElement()
    {
        var content = new byte[100]; // well under 64 KiB
        var doc     = MsixPacker.BuildBlockMap([("file.bin", content)]);

        XNamespace ns    = "http://schemas.microsoft.com/appx/2010/blockmap";
        var fileEl  = doc.Root!.Elements(ns + "File").Single();
        fileEl.Elements(ns + "Block").Should().HaveCount(1);
    }

    [Fact]
    public void BuildBlockMap_FileSpanningTwoBlocks_HasTwoBlockElements()
    {
        var content = new byte[MsixPacker.BlockSize + 1]; // forces a second block
        var doc     = MsixPacker.BuildBlockMap([("file.bin", content)]);

        XNamespace ns   = "http://schemas.microsoft.com/appx/2010/blockmap";
        var fileEl = doc.Root!.Elements(ns + "File").Single();
        fileEl.Elements(ns + "Block").Should().HaveCount(2);
    }

    [Fact]
    public void BuildBlockMap_BlockHashMatchesSha256OfChunk()
    {
        var content    = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var expected   = Convert.ToBase64String(SHA256.HashData(content));
        var doc        = MsixPacker.BuildBlockMap([("file.bin", content)]);

        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";
        var hash = doc.Root!
            .Element(ns + "File")!
            .Element(ns + "Block")!
            .Attribute("Hash")!.Value;

        hash.Should().Be(expected);
    }

    [Fact]
    public void BuildBlockMap_MultipleBlocks_EachHashMatchesItsChunk()
    {
        // Two blocks: first full-size, second one byte
        var content        = new byte[MsixPacker.BlockSize + 1];
        new Random(42).NextBytes(content);
        var expectedFirst  = Convert.ToBase64String(SHA256.HashData(content.AsSpan(0, MsixPacker.BlockSize)));
        var expectedSecond = Convert.ToBase64String(SHA256.HashData(content.AsSpan(MsixPacker.BlockSize, 1)));

        var doc = MsixPacker.BuildBlockMap([("file.bin", content)]);

        XNamespace ns     = "http://schemas.microsoft.com/appx/2010/blockmap";
        var blocks = doc.Root!.Element(ns + "File")!.Elements(ns + "Block").ToList();

        blocks[0].Attribute("Hash")!.Value.Should().Be(expectedFirst);
        blocks[1].Attribute("Hash")!.Value.Should().Be(expectedSecond);
    }

    [Fact]
    public void BuildBlockMap_ZeroByteFile_HasNoBlockChildren()
    {
        var doc = MsixPacker.BuildBlockMap([("empty.bin", Array.Empty<byte>())]);

        XNamespace ns   = "http://schemas.microsoft.com/appx/2010/blockmap";
        var fileEl = doc.Root!.Elements(ns + "File").Single();

        fileEl.Attribute("Size")!.Value.Should().Be("0");
        fileEl.Elements(ns + "Block").Should().BeEmpty();
    }

    [Fact]
    public void BuildBlockMap_LfhSizeEqualsThirtyPlusUtf8NameLength()
    {
        // "file.bin" is 8 ASCII bytes → LfhSize = 38
        var doc = MsixPacker.BuildBlockMap([("file.bin", new byte[1])]);

        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";
        var lfh = doc.Root!.Element(ns + "File")!.Attribute("LfhSize")!.Value;

        lfh.Should().Be("38"); // 30 + 8
    }

    [Fact]
    public void BuildBlockMap_LfhSize_MultibyteUtf8PathIsAccountedFor()
    {
        // "日本語/ファイル.bin" contains multi-byte UTF-8 characters
        var name = "日本語/file.bin";
        var doc  = MsixPacker.BuildBlockMap([(name, new byte[1])]);

        XNamespace ns      = "http://schemas.microsoft.com/appx/2010/blockmap";
        var lfhValue  = int.Parse(doc.Root!.Element(ns + "File")!.Attribute("LfhSize")!.Value);
        var expected  = MsixPacker.ComputeLfhSize(name);

        lfhValue.Should().Be(expected);
    }

    [Fact]
    public void BuildBlockMap_ExcludesBlockMapSignatureAndContentTypes()
    {
        var files = new (string, byte[])[]
        {
            ("AppxBlockMap.xml",    new byte[1]),
            ("AppxSignature.p7x",  new byte[1]),
            ("[Content_Types].xml", new byte[1]),
            ("AppxManifest.xml",   new byte[1]),
        };

        var doc = MsixPacker.BuildBlockMap(files);
        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";

        var fileNames = doc.Root!.Elements(ns + "File")
            .Select(f => f.Attribute("Name")!.Value)
            .ToList();

        fileNames.Should().ContainSingle("AppxManifest.xml");
        fileNames.Should().NotContain("AppxBlockMap.xml");
        fileNames.Should().NotContain("AppxSignature.p7x");
        fileNames.Should().NotContain("[Content_Types].xml");
    }

    [Fact]
    public void BuildBlockMap_FilesAreOrderedAlphabetically()
    {
        var files = new (string, byte[])[]
        {
            ("z.bin", new byte[1]),
            ("a.bin", new byte[1]),
            ("m.bin", new byte[1]),
        };

        var doc = MsixPacker.BuildBlockMap(files);
        XNamespace ns    = "http://schemas.microsoft.com/appx/2010/blockmap";
        var names = doc.Root!.Elements(ns + "File")
            .Select(f => f.Attribute("Name")!.Value)
            .ToList();

        names.Should().Equal("a.bin", "m.bin", "z.bin");
    }

    // ── ComputeLfhSize ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.bin",           35)]  // 30 + 5
    [InlineData("file.msix",       39)]  // 30 + 9
    [InlineData("Assets/Logo.png", 45)] // 30 + 15
    public void ComputeLfhSize_AsciiNames(string name, int expected)
        => MsixPacker.ComputeLfhSize(name).Should().Be(expected);

    // ── PackDirectoryAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task PackDirectoryAsync_OutputContainsBlockMap()
    {
        var srcDir = MakeTempSubDir("src");
        File.WriteAllText(Path.Combine(srcDir, "[Content_Types].xml"), MinimalContentTypes());
        File.WriteAllText(Path.Combine(srcDir, "AppxManifest.xml"), MinimalManifest("CN=Test"));
        File.WriteAllBytes(Path.Combine(srcDir, "data.bin"), new byte[] { 1, 2, 3 });

        var destMsix = TempMsix();
        await MsixPacker.PackDirectoryAsync(srcDir, destMsix);

        using var zip = ZipFile.OpenRead(destMsix);
        zip.GetEntry("AppxBlockMap.xml").Should().NotBeNull();
    }

    [Fact]
    public async Task PackDirectoryAsync_BlockMapDoesNotContainBlockMapItself()
    {
        var srcDir = MakeTempSubDir("src2");
        File.WriteAllText(Path.Combine(srcDir, "[Content_Types].xml"), MinimalContentTypes());
        File.WriteAllText(Path.Combine(srcDir, "AppxManifest.xml"), MinimalManifest("CN=Test"));

        var destMsix = TempMsix();
        await MsixPacker.PackDirectoryAsync(srcDir, destMsix);

        using var zip    = ZipFile.OpenRead(destMsix);
        var bmEntry = zip.GetEntry("AppxBlockMap.xml")!;
        using var stream = bmEntry.Open();
        var doc          = XDocument.Load(stream);

        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";
        doc.Root!.Elements(ns + "File")
            .Should().NotContain(f => f.Attribute("Name")!.Value
                .Equals("AppxBlockMap.xml", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string TempMsix(string? name = null)
        => Path.Combine(_tempDir, (name ?? Guid.NewGuid().ToString("N")) + ".msix");

    private string MakeTempSubDir(string name)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CreateMsix(
        string publisher,
        Dictionary<string, byte[]>? payloadFiles = null,
        bool includeSignature = false)
    {
        var path = TempMsix();
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        AddTextEntry(archive, "[Content_Types].xml", MinimalContentTypes());
        AddTextEntry(archive, "AppxManifest.xml",    MinimalManifest(publisher));

        foreach (var (name, content) in payloadFiles ?? new())
            AddBinaryEntry(archive, name, content);

        if (includeSignature)
            AddBinaryEntry(archive, "AppxSignature.p7x", new byte[] { 0xDE, 0xAD });

        // Minimal block map
        AddTextEntry(archive, "AppxBlockMap.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="no"?><BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap" HashMethod="http://www.w3.org/2001/04/xmlenc#sha256"/>""");

        return path;
    }

    private static void AddTextEntry(ZipArchive archive, string name, string text)
        => AddBinaryEntry(archive, name, Encoding.UTF8.GetBytes(text));

    private static void AddBinaryEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string MinimalManifest(string publisher) =>
        $"""<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="TestApp" Publisher="{publisher}" Version="1.0.0.0" ProcessorArchitecture="x64"/></Package>""";

    private static string MinimalContentTypes() =>
        """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""";
}
