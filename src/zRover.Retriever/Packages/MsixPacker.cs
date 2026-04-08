using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace zRover.Retriever.Packages;

/// <summary>
/// Pure-.NET MSIX packing utility that emulates <c>makeappx.exe pack</c>
/// from the Windows SDK.
///
/// Specifically replaces the following CLI invocations:
/// <code>
///   makeappx pack /d &lt;sourceDir&gt; /p &lt;output.msix&gt; /o
/// </code>
///
/// The key responsibility beyond simple zip creation is generating a correct
/// <c>AppxBlockMap.xml</c>, which records per-file SHA-256 block hashes (64 KiB
/// blocks) together with the local-file-header size (<c>LfhSize</c>) that
/// .NET's <see cref="ZipArchive"/> will write for each entry. The block map is
/// later consumed by <see cref="MsixSigner"/> to compute the package-content
/// digest used in <c>AppxSignature.p7x</c>.
/// </summary>
public static class MsixPacker
{
    /// <summary>Block size used when computing per-file block hashes (64 KiB).</summary>
    public const int BlockSize = 65536;

    /// <summary>
    /// Files that are excluded from <c>AppxBlockMap.xml</c> per the MSIX spec.
    /// </summary>
    private static readonly HashSet<string> s_blockMapExclusions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "AppxBlockMap.xml",
            "AppxSignature.p7x",
            "[Content_Types].xml",
        };

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>Publisher</c> attribute from <c>AppxManifest.xml</c>
    /// inside the MSIX zip.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The manifest or its Publisher attribute is missing.
    /// </exception>
    public static string ReadPublisher(string msixPath)
    {
        using var zip   = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry("AppxManifest.xml")
            ?? throw new InvalidOperationException("AppxManifest.xml not found in package.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        return doc.Root
            ?.Element(ns + "Identity")
            ?.Attribute("Publisher")
            ?.Value
            ?? throw new InvalidOperationException(
                "Could not read Publisher from AppxManifest.xml.");
    }

    /// <summary>
    /// Patches <c>Publisher</c> in <c>AppxManifest.xml</c>, regenerates
    /// <c>AppxBlockMap.xml</c>, removes <c>AppxSignature.p7x</c>, and
    /// overwrites <paramref name="msixPath"/> in-place.
    ///
    /// Emulates the workflow of manually editing the manifest and then running:
    /// <code>makeappx pack /d &lt;dir&gt; /p &lt;out.msix&gt; /o</code>
    /// </summary>
    public static async Task RepackWithPatchedPublisherAsync(
        string msixPath,
        string newPublisher,
        CancellationToken ct = default)
    {
        var tempDir  = Path.Combine(Path.GetTempPath(), $"zrover-extract-{Guid.NewGuid():N}");
        var tempMsix = Path.Combine(Path.GetTempPath(), $"zrover-repack-{Guid.NewGuid():N}.msix");

        try
        {
            // 1. Extract existing package
            ZipFile.ExtractToDirectory(msixPath, tempDir, overwriteFiles: true);

            // 2. Patch publisher in manifest
            PatchManifestPublisher(Path.Combine(tempDir, "AppxManifest.xml"), newPublisher);

            // 3. Remove old signature (will be applied again by MsixSigner)
            var sigPath = Path.Combine(tempDir, "AppxSignature.p7x");
            if (File.Exists(sigPath)) File.Delete(sigPath);

            // 3b. Strip the AppxSignature.p7x Override from [Content_Types].xml so the
            //     AppX SIP does not see a stale content-type entry when signing the
            //     repacked (unsigned) package. Leaving it in causes 0x80080209.
            var ctPath = Path.Combine(tempDir, "[Content_Types].xml");
            if (File.Exists(ctPath))
            {
                var ctDoc = XDocument.Load(ctPath);
                XNamespace ctNs = "http://schemas.openxmlformats.org/package/2006/content-types";
                ctDoc.Root?
                    .Elements(ctNs + "Override")
                    .FirstOrDefault(e => string.Equals(
                        (string?)e.Attribute("PartName"),
                        "/AppxSignature.p7x",
                        StringComparison.OrdinalIgnoreCase))
                    ?.Remove();
                ctDoc.Save(ctPath);
            }

            // 4. Pack directory into a new MSIX with fresh AppxBlockMap.xml
            await PackDirectoryAsync(tempDir, tempMsix, ct);

            // 5. Atomically replace the original file
            File.Move(tempMsix, msixPath, overwrite: true);
        }
        finally
        {
            CleanupSafe(tempDir,  isDirectory: true);
            CleanupSafe(tempMsix, isDirectory: false);
        }
    }

    // ── Internal helpers (internal for unit-test access) ────────────────────

    /// <summary>
    /// Creates an MSIX zip from all files in <paramref name="sourceDir"/>,
    /// writing a freshly computed <c>AppxBlockMap.xml</c>.
    /// Any existing <c>AppxBlockMap.xml</c> or <c>AppxSignature.p7x</c> in
    /// the source directory is replaced/omitted.
    ///
    /// Emulates: <c>makeappx pack /d &lt;sourceDir&gt; /p &lt;destMsix&gt; /o</c>
    /// </summary>
    internal static async Task PackDirectoryAsync(
        string sourceDir, string destMsix, CancellationToken ct = default)
    {
        // Enumerate and sort files; normalise separators to forward-slash (zip convention)
        var allFiles = Directory
            .GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(fullPath =>
            {
                var rel = Path.GetRelativePath(sourceDir, fullPath).Replace('\\', '/');
                return (rel, fullPath);
            })
            .OrderBy(f => f.rel, StringComparer.Ordinal)
            .ToList();

        // Read content for block-map entries (everything except the three excluded files)
        var blockMapEntries = new List<(string name, byte[] content)>();
        foreach (var (rel, fullPath) in allFiles)
        {
            if (s_blockMapExclusions.Contains(rel)) continue;
            blockMapEntries.Add((rel, await File.ReadAllBytesAsync(fullPath, ct)));
        }

        // Compute fresh AppxBlockMap.xml
        var blockMapDoc   = BuildBlockMap(blockMapEntries);
        var blockMapBytes = SerializeXml(blockMapDoc);

        // Read [Content_Types].xml bytes up front (it is excluded from blockMapEntries).
        var contentTypesFile = allFiles.FirstOrDefault(
            f => f.rel.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
        byte[]? contentTypesBytes = contentTypesFile != default
            ? await File.ReadAllBytesAsync(contentTypesFile.fullPath, ct)
            : null;

        // Build the ZIP using a custom writer that matches makeappx.exe's format exactly.
        //
        // APPX spec + makeappx ZIP-ordering contract:
        //   1. Non-manifest content files in ascending ordinal-alphabetical order.
        //   2. AppxManifest.xml MUST be the last content file (APPX spec requirement).
        //   3. AppxBlockMap.xml immediately after AppxManifest.xml.
        //   4. [Content_Types].xml immediately after AppxBlockMap.xml.
        //   5. AppxSignature.p7x last  (added later by MsixSigner, not here).
        //
        // makeappx format per entry (required for APPX SIP to recognise the file):
        //   – LFH:  version-needed=0x2D (45), flags=0x0008 (data descriptor present),
        //           method=8 (DEFLATE) or 0 (STORED), CRC32=0, sizes=0 in descriptor.
        //   – File data (DEFLATE-compressed or STORED).
        //   – Data descriptor: 0x504B0708 + CRC32(4B) + compressed(8B) + uncompressed(8B).
        //   – Central dir with version-made/needed=45, actual CRC32/sizes, 4-byte LFH offset.
        //   – Zip64 EOCD record + Zip64 EOCD locator + standard EOCD (0xFF markers).
        var now = DateTime.Now;
        ushort dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort dosDate = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);

        await using var fs = new FileStream(
            destMsix, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        var cdEntries = new List<(string name, uint crc32, uint csz, uint usz, ushort time, ushort date, uint lfhOff, bool isStd)>();

        // 1. Non-manifest content files in ordinal-alphabetical order (exclude AppxManifest.xml)
        foreach (var (name, data) in blockMapEntries
            .Where(e => !e.name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            WriteAppxZipEntry(bw, name, data, dosTime, dosDate, cdEntries);
        }

        // 2. AppxManifest.xml MUST be last among content files (APPX spec requirement)
        var manifest = blockMapEntries.FirstOrDefault(
            e => e.name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest != default)
            WriteAppxZipEntry(bw, manifest.name, manifest.content, dosTime, dosDate, cdEntries);

        // 3. AppxBlockMap.xml (immediately after AppxManifest.xml)
        WriteAppxZipEntry(bw, "AppxBlockMap.xml", blockMapBytes, dosTime, dosDate, cdEntries);

        // 4. [Content_Types].xml — written in standard format (version=20, no data descriptor)
        //    because the APPX package reader reads this file using LFH sizes directly.
        if (contentTypesBytes != null)
            WriteStandardZipEntry(bw, "[Content_Types].xml", contentTypesBytes, dosTime, dosDate, cdEntries);

        // Central directory
        long cdStart = fs.Position;
        foreach (var cd in cdEntries)
            WriteAppxCdEntry(bw, cd);
        long cdSize = fs.Position - cdStart;

        // Zip64 EOCD record
        long eocd64Off = fs.Position;
        WriteZip64Eocd(bw, cdEntries.Count, cdSize, cdStart);

        // Zip64 EOCD locator
        WriteZip64EocdLocator(bw, eocd64Off);

        // Standard EOCD (with Zip64 sentinel values)
        WriteStdEocd(bw);
    }

    // ── Custom ZIP writer (makeappx-compatible format) ──────────────────────

    /// <summary>
    /// Writes one ZIP entry in makeappx-compatible format:
    /// LFH (ver=45, flags=0x0008, STORED, zeros for CRC/size) + data +
    /// Zip64 data descriptor (24 bytes).
    ///
    /// Always uses STORED (method=0): the APPX block-map spec requires a <c>Block/@Size</c>
    /// attribute for every DEFLATE-compressed block, and that attribute must equal the
    /// compressed size of the individual per-block DEFLATE stream (not a whole-file stream).
    /// Using STORED avoids the need for <c>Block/@Size</c> entirely — the APPX SIP simply
    /// reads each 64 KiB chunk directly and verifies the SHA-256 hash against the block map.
    /// </summary>
    /// <summary>
    /// Writes one ZIP entry in standard (non-Zip64) format: version=20, no data descriptor,
    /// with actual CRC-32 and sizes stored directly in the LFH.
    /// Used for <c>[Content_Types].xml</c> and <c>AppxSignature.p7x</c> to match makeappx
    /// exactly — the APPX package reader locates these files using the LFH sizes directly.
    /// </summary>
    internal static void WriteStandardZipEntry(
        BinaryWriter bw,
        string name,
        byte[] data,
        ushort dosTime,
        ushort dosDate,
        List<(string name, uint crc32, uint csz, uint usz, ushort time, ushort date, uint lfhOff, bool isStd)> cdEntries)
    {
        uint lfhOff   = (uint)bw.BaseStream.Position;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        uint crc32    = ComputeCrc32(data);

        byte[] compressed = DeflateData(data);
        bool   useDeflate = compressed.Length < data.Length;
        byte[] fileBytes  = useDeflate ? compressed : data;
        ushort method     = useDeflate ? (ushort)0x0008 : (ushort)0x0000;

        // Local File Header: version=20, flags=0 (no data descriptor), actual CRC/sizes
        bw.Write(0x04034B50u);              // PK\x03\x04
        bw.Write((ushort)0x0014);           // version needed = 20
        bw.Write((ushort)0x0000);           // flags = 0 (no data descriptor)
        bw.Write(method);
        bw.Write(dosTime);
        bw.Write(dosDate);
        bw.Write(crc32);                    // CRC-32 in LFH
        bw.Write((uint)fileBytes.Length);   // compressed size in LFH
        bw.Write((uint)data.Length);        // uncompressed size in LFH
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)0);                // extra field length = 0
        bw.Write(nameBytes);

        // File data (no data descriptor follows)
        bw.Write(fileBytes);

        cdEntries.Add((name, crc32, (uint)fileBytes.Length, (uint)data.Length, dosTime, dosDate, lfhOff, isStd: true));
    }

    internal static void WriteAppxZipEntry(
        BinaryWriter bw,
        string name,
        byte[] data,
        ushort dosTime,
        ushort dosDate,
        List<(string name, uint crc32, uint csz, uint usz, ushort time, ushort date, uint lfhOff, bool isStd)> cdEntries)
    {
        uint lfhOff   = (uint)bw.BaseStream.Position;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        uint crc32    = ComputeCrc32(data);

        // Always STORED: DEFLATE requires per-block DEFLATE streams and Block/@Size
        // attributes in the block map. STORED avoids that complexity entirely and is
        // fully valid per the APPX spec (Block/@Size is absent for uncompressed blocks).
        byte[] fileBytes   = data;
        ushort method      = (ushort)0x0000; // STORED

        // Local File Header
        bw.Write(0x04034B50u);              // PK\x03\x04
        bw.Write((ushort)0x002D);           // version needed = 45 (Zip64)
        bw.Write((ushort)0x0008);           // flags = data-descriptor present
        bw.Write(method);                   // compression method = STORED
        bw.Write(dosTime);
        bw.Write(dosDate);
        bw.Write(0u);                       // CRC-32        = 0 (in descriptor)
        bw.Write(0u);                       // compressed sz = 0 (in descriptor)
        bw.Write(0u);                       // uncompressed  = 0 (in descriptor)
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)0);                // extra field length = 0
        bw.Write(nameBytes);

        // File data
        bw.Write(fileBytes);

        // Zip64 data descriptor: sig + CRC32(4B) + compSz(8B) + uncompSz(8B)
        bw.Write(0x08074B50u);              // PK\x07\x08
        bw.Write(crc32);
        bw.Write((ulong)fileBytes.Length);  // compressed size (8 bytes, Zip64)
        bw.Write((ulong)data.Length);       // uncompressed size (8 bytes, Zip64)

        cdEntries.Add((name, crc32, (uint)fileBytes.Length, (uint)data.Length, dosTime, dosDate, lfhOff, isStd: false));
    }

    /// <summary>Compresses <paramref name="data"/> using raw DEFLATE (no zlib header).</summary>
    private static byte[] DeflateData(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Writes one central-directory file header.</summary>
    internal static void WriteAppxCdEntry(
        BinaryWriter bw,
        (string name, uint crc32, uint csz, uint usz, ushort time, ushort date, uint lfhOff, bool isStd) cd)
    {
        var nameBytes = Encoding.UTF8.GetBytes(cd.name);
        ushort method    = cd.csz == cd.usz ? (ushort)0x0000 : (ushort)0x0008;
        ushort verMade   = cd.isStd ? (ushort)0x0014 : (ushort)0x002D;
        ushort verNeeded = cd.isStd ? (ushort)0x0014 : (ushort)0x002D;
        ushort flags     = cd.isStd ? (ushort)0x0000 : (ushort)0x0008;

        bw.Write(0x02014B50u);              // PK\x01\x02
        bw.Write(verMade);
        bw.Write(verNeeded);
        bw.Write(flags);
        bw.Write(method);
        bw.Write(cd.time);
        bw.Write(cd.date);
        bw.Write(cd.crc32);
        bw.Write(cd.csz);                   // compressed size (4 bytes)
        bw.Write(cd.usz);                   // uncompressed size (4 bytes)
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)0);                // extra field length = 0
        bw.Write((ushort)0);                // file comment length = 0
        bw.Write((ushort)0);                // disk number start = 0
        bw.Write((ushort)0);                // internal attributes = 0
        bw.Write(0u);                       // external attributes = 0
        bw.Write(cd.lfhOff);                // relative offset of LFH (4 bytes)
        bw.Write(nameBytes);
    }

    /// <summary>Writes a Zip64 end-of-central-directory record (56 bytes).</summary>
    internal static void WriteZip64Eocd(BinaryWriter bw, int count, long cdSize, long cdStart)
    {
        bw.Write(0x06064B50u);              // PK\x06\x06
        bw.Write(44UL);                     // size of remaining record = 44
        bw.Write((ushort)0x002D);           // version made by  = 45
        bw.Write((ushort)0x002D);           // version needed   = 45
        bw.Write(0u);                       // this disk = 0
        bw.Write(0u);                       // disk with start of CD = 0
        bw.Write((ulong)count);             // entries on this disk
        bw.Write((ulong)count);             // total entries
        bw.Write((ulong)cdSize);
        bw.Write((ulong)cdStart);
    }

    /// <summary>Writes a Zip64 end-of-central-directory locator (20 bytes).</summary>
    internal static void WriteZip64EocdLocator(BinaryWriter bw, long eocd64Off)
    {
        bw.Write(0x07064B50u);              // PK\x06\x07
        bw.Write(0u);                       // disk with Zip64 EOCD = 0
        bw.Write((ulong)eocd64Off);
        bw.Write(1u);                       // total disks = 1
    }

    /// <summary>
    /// Writes the standard EOCD (22 bytes) with Zip64 sentinel values
    /// (0xFFFF/0xFFFFFFFF) to indicate that Zip64 extensions must be used.
    /// </summary>
    internal static void WriteStdEocd(BinaryWriter bw)
    {
        bw.Write(0x06054B50u);              // PK\x05\x06
        bw.Write((ushort)0);               // this disk = 0
        bw.Write((ushort)0);               // disk with start of CD = 0
        bw.Write(ushort.MaxValue);          // entries on disk  = 0xFFFF (Zip64)
        bw.Write(ushort.MaxValue);          // total entries    = 0xFFFF (Zip64)
        bw.Write(uint.MaxValue);            // size of CD       = 0xFFFFFFFF (Zip64)
        bw.Write(uint.MaxValue);            // offset of CD     = 0xFFFFFFFF (Zip64)
        bw.Write((ushort)0);               // comment length = 0
    }

    /// <summary>Computes the standard IEEE 802.3 CRC-32 checksum.</summary>
    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
        }
        return ~crc;
    }

    // ── Block map ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <c>AppxBlockMap.xml</c> document from the provided file entries.
    ///
    /// Block-map rules (MSIX spec):
    /// <list type="bullet">
    ///   <item>Block size: 65536 bytes.</item>
    ///   <item>Hash: base64(SHA-256(chunk bytes)).</item>
    ///   <item><c>File/@Size</c>: uncompressed byte count.</item>
    ///   <item>
    ///     <c>File/@LfhSize</c>: 30 + UTF-8 byte length of the entry name.
    ///     (30-byte fixed ZIP local-file-header + variable-length name field;
    ///     .NET's <see cref="ZipArchive"/> emits no extra fields for normal entries.)
    ///   </item>
    ///   <item>Files sorted: non-manifest files ordinal alphabetically, <c>AppxManifest.xml</c> last (matching ZIP entry order per APPX spec).</item>
    ///   <item>
    ///     Excluded from map: <c>AppxBlockMap.xml</c>, <c>AppxSignature.p7x</c>,
    ///     <c>[Content_Types].xml</c>.
    ///   </item>
    ///   <item>Zero-byte files produce a <c>&lt;File&gt;</c> with no <c>&lt;Block&gt;</c> children.</item>
    /// </list>
    /// </summary>
    internal static XDocument BuildBlockMap(
        IEnumerable<(string name, byte[] content)> files)
    {
        XNamespace ns  = "http://schemas.microsoft.com/appx/2010/blockmap";
        XNamespace b4  = "http://schemas.microsoft.com/appx/2021/blockmap";

        var fileElements = files
            .Where(f => !s_blockMapExclusions.Contains(f.name))
            // APPX spec requires block-map entry order to match ZIP entry order.
            // Non-manifest files first (ordinal alphabetical), AppxManifest.xml last.
            .OrderBy(f => f.name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => f.name, StringComparer.Ordinal)
            .Select(f => BuildFileElement(ns, f.name, f.content));

        // Attribute ordering matches makeappx.exe exactly:
        //   xmlns, xmlns:b4, IgnorableNamespaces, HashMethod
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(ns + "BlockMap",
                new XAttribute(XNamespace.Xmlns + "b4", b4.NamespaceName),
                new XAttribute("IgnorableNamespaces", "b4"),
                new XAttribute("HashMethod", "http://www.w3.org/2001/04/xmlenc#sha256"),
                fileElements));
    }

    /// <summary>
    /// Returns the Local File Header (LFH) size in bytes for a zip entry whose
    /// name is <paramref name="entryName"/> as emitted by .NET's
    /// <see cref="ZipArchive"/> (no extra-field bytes for non-Zip64 entries).
    ///
    /// ZIP LFH layout: 30-byte fixed signature + file-name bytes (UTF-8).
    /// </summary>
    internal static int ComputeLfhSize(string entryName)
        => 30 + Encoding.UTF8.GetByteCount(entryName);

    // ── Private helpers ─────────────────────────────────────────────────────

    private static XElement BuildFileElement(XNamespace ns, string name, byte[] content)
    {
        var blocks  = new List<XElement>();
        int offset  = 0;

        // Zero-byte files: <File> element with no <Block> children
        while (offset < content.Length)
        {
            int   len  = Math.Min(BlockSize, content.Length - offset);
            var   hash = SHA256.HashData(content.AsSpan(offset, len));
            blocks.Add(new XElement(ns + "Block",
                new XAttribute("Hash", Convert.ToBase64String(hash))));
            offset += len;
        }

        // APPX spec requires backslashes in block-map file names (ZIP uses forward slashes)
        var blockMapName = name.Replace('/', '\\');
        return new XElement(ns + "File",
            new XAttribute("Name",    blockMapName),
            new XAttribute("Size",    content.Length),
            new XAttribute("LfhSize", ComputeLfhSize(name)),
            blocks);
    }

    private static void PatchManifestPublisher(string manifestPath, string newPublisher)
    {
        var doc = XDocument.Load(manifestPath);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var attr = doc.Root
            ?.Element(ns + "Identity")
            ?.Attribute("Publisher")
            ?? throw new InvalidOperationException(
                "Could not find Identity/@Publisher in AppxManifest.xml.");
        attr.SetValue(newPublisher);
        doc.Save(manifestPath);
    }

    private static byte[] SerializeXml(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = false,
            NewLineHandling = System.Xml.NewLineHandling.None,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var xmlWriter = System.Xml.XmlWriter.Create(ms, settings))
            doc.Save(xmlWriter);
        return ms.ToArray();
    }

    private static void CleanupSafe(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (!isDirectory && File.Exists(path))
                File.Delete(path);
        }
        catch { /* best-effort cleanup */ }
    }
}
