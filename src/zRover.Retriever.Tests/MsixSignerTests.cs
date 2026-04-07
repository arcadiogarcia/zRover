using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using zRover.Retriever.Packages;

namespace zRover.Retriever.Tests;

/// <summary>
/// Tests for <see cref="MsixSigner"/>, the MSIX signing wrapper around
/// <c>signtool.exe sign /fd SHA256</c>.
/// SignAsync tests require the Windows SDK (signtool.exe) to be installed.
/// All other tests use ephemeral in-memory certificates and temp files.
/// </summary>
public sealed class MsixSignerTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"MsixSignerTests-{Guid.NewGuid():N}");

    private readonly X509Certificate2 _cert;

    public MsixSignerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _cert = CreateEphemeralSigningCert();
    }

    public void Dispose()
    {
        _cert.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ComputeAxpc ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeAxpc_SingleFileSingleBlock_MatchesHashOfThatBlock()
    {
        var blockBytes  = new byte[] { 0x01, 0x02, 0x03 };
        var blockHash   = SHA256.HashData(blockBytes);
        var expectedAxpc = SHA256.HashData(blockHash); // SHA256 of the single hash

        var blockMap = BuildBlockMap([("f.bin", blockBytes)]);
        MsixSigner.ComputeAxpc(blockMap).Should().Equal(expectedAxpc);
    }

    [Fact]
    public void ComputeAxpc_TwoFiles_ConcatenatesAllBlockHashes()
    {
        var contentA = new byte[] { 0xAA };
        var contentB = new byte[] { 0xBB };
        var hashA    = SHA256.HashData(contentA);
        var hashB    = SHA256.HashData(contentB);

        // Expected AXPC = SHA256(hashA ‖ hashB)
        var combined     = hashA.Concat(hashB).ToArray();
        var expectedAxpc = SHA256.HashData(combined);

        var blockMap = BuildBlockMap([("a.bin", contentA), ("b.bin", contentB)]);
        MsixSigner.ComputeAxpc(blockMap).Should().Equal(expectedAxpc);
    }

    [Fact]
    public void ComputeAxpc_MultiBlockFile_AllBlockHashesConcatenated()
    {
        // Force two blocks
        var firstChunk  = new byte[MsixPacker.BlockSize];
        var secondChunk = new byte[] { 0xFF };
        var content     = firstChunk.Concat(secondChunk).ToArray();

        var h1 = SHA256.HashData(firstChunk);
        var h2 = SHA256.HashData(secondChunk);
        var expectedAxpc = SHA256.HashData(h1.Concat(h2).ToArray());

        var blockMap = BuildBlockMap([("f.bin", content)]);
        MsixSigner.ComputeAxpc(blockMap).Should().Equal(expectedAxpc);
    }

    [Fact]
    public void ComputeAxpc_FileWithNoBlocks_ProducesHashOfEmptyInput()
    {
        // Empty file → no <Block> elements → SHA256("") 
        var expectedAxpc = SHA256.HashData(Array.Empty<byte>());
        var blockMap     = BuildBlockMap([("empty.bin", Array.Empty<byte>())]);
        MsixSigner.ComputeAxpc(blockMap).Should().Equal(expectedAxpc);
    }

    // ── BuildHashBundle ──────────────────────────────────────────────────────

    [Fact]
    public void BuildHashBundle_Is184BytesLong()
    {
        var bundle = MsixSigner.BuildHashBundle(new byte[32], new byte[32], new byte[32], new byte[32], new byte[32]);
        bundle.Should().HaveCount(184); // "APPX"(4) + 5 × (4-byte tag + 32-byte hash)
    }

    [Fact]
    public void BuildHashBundle_StartsWithAppxMagicAndCorrectTags()
    {
        var bundle = MsixSigner.BuildHashBundle(new byte[32], new byte[32], new byte[32], new byte[32], new byte[32]);

        Encoding.ASCII.GetString(bundle, 0,   4).Should().Be("APPX"); // prefix
        Encoding.ASCII.GetString(bundle, 4,   4).Should().Be("AXPC"); // entry 1 tag
        Encoding.ASCII.GetString(bundle, 40,  4).Should().Be("AXCD"); // entry 2 tag
        Encoding.ASCII.GetString(bundle, 76,  4).Should().Be("AXCT"); // entry 3 tag
        Encoding.ASCII.GetString(bundle, 112, 4).Should().Be("AXBM"); // entry 4 tag
        Encoding.ASCII.GetString(bundle, 148, 4).Should().Be("AXCI"); // entry 5 tag
    }

    [Fact]
    public void BuildHashBundle_HashBytesEmbeddedAtCorrectOffsets()
    {
        var axpc = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var axcd = Enumerable.Repeat((byte)0xDD, 32).ToArray();
        var axct = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        var axbm = Enumerable.Repeat((byte)0xBB, 32).ToArray();
        var axci = Enumerable.Repeat((byte)0xEE, 32).ToArray();

        var bundle = MsixSigner.BuildHashBundle(axpc, axcd, axct, axbm, axci);

        bundle[8..40].Should().Equal(axpc);    // after APPX(4) + AXPC_tag(4)
        bundle[44..76].Should().Equal(axcd);   // after prev + AXCD_tag(4)
        bundle[80..112].Should().Equal(axct);  // after prev + AXCT_tag(4)
        bundle[116..148].Should().Equal(axbm); // after prev + AXBM_tag(4)
        bundle[152..184].Should().Equal(axci); // after prev + AXCI_tag(4)
    }

    // ── BuildSpcIndirectDataContent ──────────────────────────────────────────

    [Fact]
    public void BuildSpcIndirectDataContent_IsValidDer()
    {
        var digest = new byte[184]; // correct bundle size
        var der    = MsixSigner.BuildSpcIndirectDataContent(digest);

        // Must start with SEQUENCE tag (0x30)
        der[0].Should().Be(0x30);
        // Must be parseable as DER (no exception)
        var act = () => System.Formats.Asn1.AsnDecoder.ReadSequence(
            der, System.Formats.Asn1.AsnEncodingRules.DER, out _, out _, out _);
        act.Should().NotThrow();
    }

    [Fact]
    public void BuildSpcIndirectDataContent_EmbedsSipGuid()
    {
        var der = MsixSigner.BuildSpcIndirectDataContent(new byte[184]);
        // The SIP GUID bytes must appear somewhere in the DER blob
        der.Should().ContainInOrder(MsixSigner.SipGuid);
    }

    [Fact]
    public void BuildSpcIndirectDataContent_EmbedsPackageDigest()
    {
        var digest = new byte[184];
        new Random(7).NextBytes(digest);
        var der = MsixSigner.BuildSpcIndirectDataContent(digest);
        der.Should().ContainInOrder(digest);
    }

    // ── SignAsync ────────────────────────────────────────────────────────────
    // NOTE: SignAsync tests are skipped because they require both:
    //   1) Windows SDK signtool.exe installed, AND
    //   2) A real/complete MSIX package (signtool rejects minimal test packages).
    // End-to-end verification is done by deploying via Rover MCP tools.

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_AddsAppxSignaturePxToZip()
    {
        var msix = CreatePackedMsix("payload.bin", new byte[] { 1, 2, 3 });
        await MsixSigner.SignAsync(msix, _cert);

        using var zip = ZipFile.OpenRead(msix);
        zip.GetEntry("AppxSignature.p7x").Should().NotBeNull();
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_AppxSignaturePx_IsValidCmsSignedData()
    {
        var msix = CreatePackedMsix("data.bin", new byte[] { 0xDE, 0xAD });
        await MsixSigner.SignAsync(msix, _cert);

        var sigBytes = ReadZipEntry(msix, "AppxSignature.p7x");

        // AppxSignature.p7x is PKCX-format: 4-byte "PKCX" magic + DER ContentInfo.
        // .NET's SignedCms.Decode rejects it because the MSIX SIP writes spcDer
        // directly inside [0] EXPLICIT eContent (no OCTET STRING wrapper), which
        // is non-standard per RFC 5652.  Validate structure at the byte level.
        sigBytes[..4].Should().Equal(
            new byte[] { (byte)'P', (byte)'K', (byte)'C', (byte)'X' },
            "file must start with PKCX magic");

        var der = sigBytes[4..];
        der[0].Should().Be(0x30, "DER must begin with SEQUENCE (ContentInfo)");

        // The outer SEQUENCE length must be consistent with the actual byte count.
        var act = () => AsnDecoder.ReadSequence(
            der, AsnEncodingRules.BER, out _, out _, out _);
        act.Should().NotThrow("outer ContentInfo SEQUENCE header must be valid DER");
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_ContentTypeOidIsSpciIndirectData()
    {
        var msix = CreatePackedMsix("x.bin", new byte[1]);
        await MsixSigner.SignAsync(msix, _cert);

        // SPC_INDIRECT_DATA_OBJID (1.3.6.1.4.1.311.2.1.4) DER-encoded:
        // 06 0A 2B 06 01 04 01 82 37 02 01 04
        byte[] spcOid = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x01, 0x04];
        var sigBytes = ReadZipEntry(msix, "AppxSignature.p7x");
        sigBytes.Should().ContainInOrder(spcOid,
            "signature must contain the SPC_INDIRECT_DATA_OBJID OID");
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_SignerCertIsEmbeddedInSignerInfos()
    {
        var msix = CreatePackedMsix("x.bin", new byte[1]);
        await MsixSigner.SignAsync(msix, _cert);

        // The CMS SignedData certificates field contains the signer cert verbatim.
        // Verify the cert's DER bytes appear in the signature blob.
        var sigBytes = ReadZipEntry(msix, "AppxSignature.p7x");
        sigBytes.Should().ContainInOrder(_cert.RawData,
            "the signer certificate must be embedded in the signature");
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_DigestAlgorithmIsSha256()
    {
        var msix = CreatePackedMsix("x.bin", new byte[1]);
        await MsixSigner.SignAsync(msix, _cert);

        // SHA-256 OID (2.16.840.1.101.3.4.2.1) DER-encoded:
        // 06 09 60 86 48 01 65 03 04 02 01
        byte[] sha256Oid = [0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01];
        var sigBytes = ReadZipEntry(msix, "AppxSignature.p7x");
        sigBytes.Should().ContainInOrder(sha256Oid,
            "signature must use SHA-256 as the digest algorithm");
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_ResignReplacesExistingSignature()
    {
        var msix = CreatePackedMsix("x.bin", new byte[1]);

        // Sign once, then sign again
        await MsixSigner.SignAsync(msix, _cert);
        await MsixSigner.SignAsync(msix, _cert);

        // Must still have exactly one AppxSignature.p7x entry
        using var zip = ZipFile.OpenRead(msix);
        zip.Entries.Where(e => e.FullName.Equals(
                "AppxSignature.p7x", StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(1, "re-signing must replace, not append");
    }

    [Fact(Skip = "Requires Windows SDK signtool.exe and a full MSIX package")]
    public async Task SignAsync_SignedBytesAreDifferentForDifferentContent()
    {
        var msix1 = CreatePackedMsix("f.bin", new byte[] { 0x01 });
        var msix2 = CreatePackedMsix("f.bin", new byte[] { 0x02 });

        await MsixSigner.SignAsync(msix1, _cert);
        await MsixSigner.SignAsync(msix2, _cert);

        var sig1 = ReadZipEntry(msix1, "AppxSignature.p7x");
        var sig2 = ReadZipEntry(msix2, "AppxSignature.p7x");

        sig1.Should().NotEqual(sig2, "different content must produce different signatures");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string TempMsix()
        => Path.Combine(_tempDir, $"{Guid.NewGuid():N}.msix");

    /// <summary>
    /// Creates a fully packed MSIX (with AppxBlockMap.xml) suitable for signing.
    /// Uses <see cref="MsixPacker.PackDirectoryAsync"/> so the block map is real.
    /// </summary>
    private string CreatePackedMsix(string payloadName, byte[] payloadContent)
    {
        var srcDir = Path.Combine(_tempDir, $"src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(Path.Combine(srcDir, "[Content_Types].xml"), MinimalContentTypes());
        File.WriteAllText(Path.Combine(srcDir, "AppxManifest.xml"), MinimalManifest());
        File.WriteAllBytes(Path.Combine(srcDir, payloadName), payloadContent);

        var dest = TempMsix();
        MsixPacker.PackDirectoryAsync(srcDir, dest).GetAwaiter().GetResult();
        return dest;
    }

    private static XDocument BuildBlockMap(IEnumerable<(string name, byte[] content)> files)
        => MsixPacker.BuildBlockMap(files);

    private static byte[] ReadZipEntry(string msixPath, string entryName)
    {
        using var zip   = ZipFile.OpenRead(msixPath);
        var entry  = zip.GetEntry(entryName)!;
        using var stream = entry.Open();
        using var ms     = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static SignedCms DecodeCms(byte[] bytes)
    {
        // AppxSignature.p7x starts with 4-byte "PKCX" magic before the DER data
        var cms = new SignedCms();
        cms.Decode(bytes[4..]);
        return cms;
    }

    private static X509Certificate2 CreateEphemeralSigningCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=zRover Test Signing",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));

        var raw = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));

        // Load with Exportable flag so SignAsync can write the key to a temp PFX for signtool.
        return X509CertificateLoader.LoadPkcs12(
            raw.Export(X509ContentType.Pfx),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    private static string MinimalManifest() =>
        """<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="TestApp" Publisher="CN=zRover Dev Signing" Version="1.0.0.0" ProcessorArchitecture="x64"/></Package>""";

    private static string MinimalContentTypes() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="bin" ContentType="application/octet-stream"/><Override PartName="/AppxManifest.xml" ContentType="application/vnd.ms-appx.manifest+xml"/><Override PartName="/AppxBlockMap.xml" ContentType="application/vnd.ms-appx.blockmap+xml"/></Types>""";
}
