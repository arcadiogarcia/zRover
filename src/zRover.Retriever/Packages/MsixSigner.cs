using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace zRover.Retriever.Packages;

/// <summary>
/// MSIX signing utility.
///
/// <see cref="SignAsync"/> calls <c>mssign32.dll!SignerSignEx2</c> directly, which
/// invokes the inbox AppX SIP (<c>appxsip.dll</c>) to compute the correct AXPC/AXCD/AXCI
/// hash bundle and write <c>AppxSignature.p7x</c>.  No SDK tools required —
/// <c>mssign32.dll</c> and <c>appxsip.dll</c> are inbox Windows components.
/// The package must not already contain <c>AppxSignature.p7x</c> when this is called.
/// </summary>
public static class MsixSigner
{
    /// <summary>
    /// The 4-byte magic that prefixes the DER-encoded SignedData inside
    /// <c>AppxSignature.p7x</c>. This is a fixed constant in the MSIX signing format.
    /// </summary>
    private static ReadOnlySpan<byte> P7xMagic => [0x50, 0x4B, 0x43, 0x58]; // "PKCX"

    /// <summary>
    /// The GUID stored in <c>SpcSipInfo.gSipGuid</c> as emitted by signtool for MSIX
    /// packages, in Windows GUID little-endian wire format.
    /// Corresponds to <c>{0AC5DF4B-CE07-4DE2-B76E-23C839A09FD1}</c> (the APPX SIP GUID).
    /// </summary>
    internal static readonly byte[] SipGuid =
    [
        0x4B, 0xDF, 0xC5, 0x0A, 0x07, 0xCE, 0xE2, 0x4D,
        0xB7, 0x6E, 0x23, 0xC8, 0x39, 0xA0, 0x9F, 0xD1
    ];

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Signs the MSIX at <paramref name="msixPath"/> in-place using
    /// <paramref name="cert"/> via a direct call to the Windows
    /// <c>mssign32.dll!SignerSignEx2</c> API, which invokes the inbox AppX SIP
    /// (<c>appxsip.dll</c>) to compute the correct hash bundle and write
    /// <c>AppxSignature.p7x</c>.  No SDK tools required.
    /// The package must be unsigned (no <c>AppxSignature.p7x</c> entry) when called.
    /// </summary>
    public static Task SignAsync(
        string msixPath,
        X509Certificate2 cert,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Call mssign32!SignerSignEx2 directly.  The AppX SIP (appxsip.dll)
        // computes the correct AXPC/AXCD/AXCI/CodeIntegrity hash bundle and writes
        // AppxSignature.p7x (and its [Content_Types].xml entry) into the MSIX.
        CallSignerSignEx2(msixPath, cert);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a structurally valid (but not Windows-verifiable) <c>AppxSignature.p7x</c>
    /// into <paramref name="msixPath"/>, updating <c>[Content_Types].xml</c> accordingly.
    /// This is the same as the old pure-.NET signing implementation and serves as a
    /// pre-signing step that lets signtool recognise the package as a signable MSIX.
    /// </summary>
    private static void PreSign(string msixPath, X509Certificate2 cert)
    {
        byte[]     blockMapBytes;
        byte[]     contentTypesBytes;
        byte[]     manifestBytes;
        byte[]?    codeIntegrityBytes;
        XDocument  blockMapDoc;

        using (var zip = ZipFile.OpenRead(msixPath))
        {
            blockMapBytes      = ReadEntry(zip, "AppxBlockMap.xml");
            contentTypesBytes  = ReadEntry(zip, "[Content_Types].xml");
            manifestBytes      = ReadEntry(zip, "AppxManifest.xml");
            codeIntegrityBytes = TryReadEntry(zip, "AppxMetadata/CodeIntegrity.cat");
            blockMapDoc        = XDocument.Parse(Encoding.UTF8.GetString(blockMapBytes));
        }

        var signedContentTypesBytes = InjectSignatureContentType(contentTypesBytes);

        var axpc = ComputeAxpc(blockMapDoc);
        var axcd = SHA256.HashData(manifestBytes);
        var axct = SHA256.HashData(signedContentTypesBytes);
        var axbm = SHA256.HashData(blockMapBytes);
        var axci = codeIntegrityBytes is null ? new byte[32] : SHA256.HashData(codeIntegrityBytes);

        var hashBundle  = BuildHashBundle(axpc, axcd, axct, axbm, axci);
        var spcDer      = BuildSpcIndirectDataContent(hashBundle);
        var signedBytes = BuildSignature(spcDer, cert);

        var tempMsix = msixPath + ".presigning.tmp";
        try
        {
            CopyZipReplaceEntry(msixPath, tempMsix, "AppxSignature.p7x", signedBytes,
                                "[Content_Types].xml", signedContentTypesBytes);
            File.Move(tempMsix, msixPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempMsix)) File.Delete(tempMsix); } catch { /* best-effort */ }
        }
    }

    // ── mssign32 P/Invoke ────────────────────────────────────────────────────

    // Struct sizes on x64 (C ABI, pointer = 8 bytes):
    //   SIGNER_FILE_INFO:       cbSize@0(4), [pad4], pwszFileName@8(8), hFile@16(8)         = 24
    //   SIGNER_SUBJECT_INFO:    cbSize@0(4), [pad4], pdwIndex@8(8), choice@16(4), [pad4], p@24(8) = 32
    //   SIGNER_CERT_STORE_INFO: cbSize@0(4), [pad4], pCtx@8(8), policy@16(4), [pad4], hStore@24(8) = 32
    //   SIGNER_CERT:            cbSize@0(4), choice@4(4), pInfo@8(8), hwnd@16(8)            = 24
    //   SIGNER_SIGNATURE_INFO:  cbSize@0(4), alg@4(4), attrChoice@8(4), [pad4], pAttr@16(8), pAuth@24(8), pUnauth@32(8) = 40

    [DllImport("mssign32.dll", EntryPoint = "SignerSignEx2", ExactSpelling = true, SetLastError = false)]
    private static extern int SignerSignEx2Native(
        uint  dwFlags,
        nint  pSubjectInfo,
        nint  pSignerCert,
        nint  pSignatureInfo,
        nint  pProviderInfo,
        uint  dwTimestampFlags,
        nint  pszAlgOid,
        nint  pwszHttpTimeStamp,
        nint  psRequest,
        nint  pSipData,
        out nint ppSignerContext,
        nint  pCryptoPolicy,
        nint  pReserved);

    [DllImport("mssign32.dll", EntryPoint = "SignerFreeSignerContext", ExactSpelling = true, SetLastError = false)]
    private static extern int SignerFreeSignerContextNative(nint pSignerContext);

    /// <summary>
    /// Calls <c>mssign32.dll!SignerSignEx2</c> to sign <paramref name="msixPath"/> using
    /// the AppX SIP installed as part of Windows.  <paramref name="cert"/> must have an
    /// accessible private key (e.g. loaded from <c>CurrentUser\My</c> store or via
    /// <see cref="X509KeyStorageFlags.Exportable"/>).
    /// </summary>
    private static void CallSignerSignEx2(string msixPath, X509Certificate2 cert)
    {
        const uint SipSubjectFile        = 1;       // SIGNER_SUBJECT_FILE
        const uint SipCertStore          = 2;       // SIGNER_CERT_STORE
        const uint SipCertPolicyChainNR  = 8;       // SIGNER_CERT_POLICY_CHAIN_NO_ROOT
        const uint CAlgSha256            = 0x800c;  // CALG_SHA_256

        const int FileInfoSize    = 24;
        const int SubjectInfoSize = 32;
        const int CertStoreSize   = 32;
        const int SignerCertSize  = 24;
        const int SigInfoSize     = 40;

        var allocs = new List<nint>();
        nint AllocZeroed(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            allocs.Add(p);
            return p;
        }

        try
        {
            // DWORD index = 0 (points into the subject array; always 0 for a single file)
            var indexPtr    = AllocZeroed(4);
            var fileNamePtr = Marshal.StringToHGlobalUni(msixPath);
            allocs.Add(fileNamePtr);

            // SIGNER_FILE_INFO
            var fileInfo = AllocZeroed(FileInfoSize);
            Marshal.WriteInt32(fileInfo,  0, FileInfoSize);  // cbSize
            Marshal.WriteIntPtr(fileInfo, 8, fileNamePtr);   // pwszFileName (@8)

            // SIGNER_SUBJECT_INFO
            var subjectInfo = AllocZeroed(SubjectInfoSize);
            Marshal.WriteInt32(subjectInfo,  0, SubjectInfoSize);          // cbSize
            Marshal.WriteIntPtr(subjectInfo, 8, indexPtr);                 // pdwIndex (@8)
            Marshal.WriteInt32(subjectInfo, 16, (int)SipSubjectFile);      // dwSubjectChoice (@16)
            Marshal.WriteIntPtr(subjectInfo,24, fileInfo);                 // pSignerFileInfo (@24)

            // SIGNER_CERT_STORE_INFO: cbSize=32, pSigningCert=cert.Handle, dwCertPolicy=8 (CHAIN_NO_ROOT), hCertStore=0
            // SIGNER_CERT_POLICY_CHAIN_NO_ROOT (8) tells mssign32 the chain is self-signed/no-root,
            // which avoids the CERT_E_CHAINING error for self-signed dev certificates.
            // cert.Handle is a PCCERT_CONTEXT owned by the X509Certificate2 object;
            // it remains valid as long as `cert` is alive (kept via GC.KeepAlive below).
            var certStoreInfo = AllocZeroed(CertStoreSize);
            Marshal.WriteInt32(certStoreInfo,  0, CertStoreSize);              // cbSize
            Marshal.WriteIntPtr(certStoreInfo, 8, cert.Handle);                // pSigningCert (@8)
            Marshal.WriteInt32(certStoreInfo, 16, (int)SipCertPolicyChainNR); // dwCertPolicy (@16)

            // SIGNER_CERT: cbSize=24, dwCertChoice=2 (STORE), pCertStoreInfo, hwnd=0
            var signerCert = AllocZeroed(SignerCertSize);
            Marshal.WriteInt32(signerCert,  0, SignerCertSize);          // cbSize
            Marshal.WriteInt32(signerCert,  4, (int)SipCertStore);      // dwCertChoice (@4)
            Marshal.WriteIntPtr(signerCert, 8, certStoreInfo);           // pCertStoreInfo (@8)

            // SIGNER_SIGNATURE_INFO
            var sigInfo = AllocZeroed(SigInfoSize);
            Marshal.WriteInt32(sigInfo, 0, SigInfoSize);           // cbSize
            Marshal.WriteInt32(sigInfo, 4, (int)CAlgSha256);      // algidHash (@4)

            int hr = SignerSignEx2Native(
                dwFlags:           0,
                pSubjectInfo:      subjectInfo,
                pSignerCert:       signerCert,
                pSignatureInfo:    sigInfo,
                pProviderInfo:     nint.Zero,
                dwTimestampFlags:  0,
                pszAlgOid:         nint.Zero,
                pwszHttpTimeStamp: nint.Zero,
                psRequest:         nint.Zero,
                pSipData:          nint.Zero,
                ppSignerContext:   out var signerCtx,
                pCryptoPolicy:     nint.Zero,
                pReserved:         nint.Zero);

            if (signerCtx != nint.Zero)
                SignerFreeSignerContextNative(signerCtx);

            GC.KeepAlive(cert); // ensure cert.Handle stays valid through the P/Invoke
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }

    // ── Internal helpers (internal for unit-test access) ────────────────────

    /// <summary>
    /// Computes <b>AXPC</b>: SHA-256 of the concatenated base64-decoded block
    /// hashes from every <c>&lt;Block&gt;</c> element in <paramref name="blockMap"/>,
    /// enumerated in document order (file order, then block order within each file).
    /// </summary>
    internal static byte[] ComputeAxpc(XDocument blockMap)
    {
        XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file  in blockMap.Root!.Elements(ns + "File"))
        foreach (var block in file.Elements(ns + "Block"))
        {
            var decoded = Convert.FromBase64String(block.Attribute("Hash")!.Value);
            hasher.AppendData(decoded);
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Builds the 184-byte APPX hash bundle used by the MSIX SIP:
    /// <c>"APPX" ‖ "AXPC"(32) ‖ "AXCD"(32) ‖ "AXCT"(32) ‖ "AXBM"(32) ‖ "AXCI"(32)</c>
    /// where each entry is a literal 4-byte ASCII tag immediately followed by 32 hash bytes
    /// (no length field). This bundle is stored verbatim as the DigestInfo digest.
    /// </summary>
    internal static byte[] BuildHashBundle(
        byte[] axpc, byte[] axcd, byte[] axct, byte[] axbm, byte[] axci)
    {
        const int EntrySize  = 4 + 32; // tag(4) + hash(32)
        using var ms = new MemoryStream(4 + 5 * EntrySize); // "APPX" + 5 entries

        static void WriteEntry(MemoryStream stream, string tag, byte[] hash)
        {
            stream.Write(Encoding.ASCII.GetBytes(tag));
            stream.Write(hash);
        }

        ms.Write(Encoding.ASCII.GetBytes("APPX")); // 4-byte magic prefix
        WriteEntry(ms, "AXPC", axpc);
        WriteEntry(ms, "AXCD", axcd);
        WriteEntry(ms, "AXCT", axct);
        WriteEntry(ms, "AXBM", axbm);
        WriteEntry(ms, "AXCI", axci);
        return ms.ToArray();
    }

    /// <summary>
    /// DER-encodes an <c>SpcIndirectDataContent</c> containing the supplied
    /// <paramref name="packageDigest"/> (32 SHA-256 bytes).
    ///
    /// ASN.1 structure (szOID_SPC_SIPINFO_OBJID = 1.3.6.1.4.1.311.2.1.30):
    /// <code>
    /// SEQUENCE {
    ///   SEQUENCE {                               -- SpcAttributeTypeAndOptionalValue
    ///     OID 1.3.6.1.4.1.311.2.1.30            -- szOID_SPC_SIPINFO_OBJID
    ///     SEQUENCE {
    ///       INTEGER 65536                        -- dwPageSize
    ///       OCTET STRING (16)                    -- gSIPGuid (LE bytes)
    ///       INTEGER 0 (×5)                       -- reserved
    ///     }
    ///   }
    ///   SEQUENCE {                               -- DigestInfo
    ///     SEQUENCE {
    ///       OID 2.16.840.1.101.3.4.2.1          -- SHA-256
    ///       NULL
    ///     }
    ///     OCTET STRING (32)                      -- packageDigest
    ///   }
    /// }
    /// </code>
    /// </summary>
    internal static byte[] BuildSpcIndirectDataContent(byte[] packageDigest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            // SpcAttributeTypeAndOptionalValue
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.30");
                using (writer.PushSequence())
                {
                    writer.WriteInteger(0x01010000L); // SIP version as written by signtool
                    writer.WriteOctetString(SipGuid);
                    writer.WriteInteger(0);
                    writer.WriteInteger(0);
                    writer.WriteInteger(0);
                    writer.WriteInteger(0);
                    writer.WriteInteger(0);
                }
            }

            // DigestInfo
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); // SHA-256
                    writer.WriteNull();
                }
                writer.WriteOctetString(packageDigest);
            }
        }

        return writer.Encode();
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Wraps <paramref name="spcDer"/> in a CMS <c>SignedData</c> signed by
    /// <paramref name="cert"/> and returns the DER-encoded <c>ContentInfo</c>.
    /// This is the raw content of <c>AppxSignature.p7x</c>.
    /// </summary>
    private static byte[] BuildSignature(byte[] spcDer, X509Certificate2 cert)
    {
        var contentInfo = new ContentInfo(
            new Oid("1.3.6.1.4.1.311.2.1.4"), // SPC_INDIRECT_DATA_OBJID
            spcDer);

        var cms    = new SignedCms(contentInfo, detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption   = X509IncludeOption.EndCertOnly,
        };

        // Add Authenticode signed attributes required by the Windows MSIX SIP.
        // Without these, the MSIX SIP cannot recognise the signature format.
        //   SPC_SP_OPUS_INFO  (1.3.6.1.4.1.311.2.1.12) – empty SEQUENCE
        //   SPC_STATEMENT_TYPE (1.3.6.1.4.1.311.2.1.11) – individual-code-signing OID
        signer.SignedAttributes.Add(
            new AsnEncodedData("1.3.6.1.4.1.311.2.1.12",
                new byte[] { 0x30, 0x00 })); // SEQUENCE {}
        signer.SignedAttributes.Add(
            new AsnEncodedData("1.3.6.1.4.1.311.2.1.11",
                new byte[] { 0x30, 0x0C, 0x06, 0x0A,
                             0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x01, 0x15 }));
        // 1.3.6.1.4.1.311.2.1.21 = SPC_INDIVIDUAL_SP_KEY_PURPOSE_OBJID

        cms.ComputeSignature(signer, silent: true);
        var derBytes = cms.Encode();

        // Fix 1: .NET sets SignedData.version=3 for non-id-data content types (RFC 5652
        //         §5.1), but the Windows MSIX SIP expects PKCS#7 version=1.  Patch the
        //         version INTEGER value in-place (it is always at a fixed offset in the
        //         ContentInfo produced by SignedCms for a single-signer, no-attribute-cert
        //         structure: ContentInfo(4) + [0](4) + SignedData(4) + ver-tag(1) +
        //         ver-len(1) = offset 14 from the start of SignedData content = byte 23 of
        //         the outer SEQUENCE content, or byte [25] of the full DER).
        // We locate it robustly rather than assuming a fixed offset.
        PatchSignedDataVersion(derBytes, 1);

        // Fix 2: .NET 9 wraps eContent for non-id-data content types in an OCTET STRING:
        //         [0] EXPLICIT { OCTET STRING { spcDer } }
        //         The APPX SIP (and signtool) expect the content directly:
        //         [0] EXPLICIT { spcDer }
        //         Strip the 4-byte OCTET STRING TLV header.
        derBytes = RemoveEContentOctetStringWrapper(derBytes);

        // Fix 3: .NET omits the SHA-256 AlgorithmIdentifier NULL parameter in
        //         digestAlgorithms.  signtool includes it; add it for compatibility.
        derBytes = AddSha256NullParameter(derBytes);

        // Fix 4: After the OCTET STRING wrapper is removed (Fix 2), .NET's SignedCms
        //         falls into PKCS#7-compat mode (_hasPkcs7Content=true) and hashes the
        //         *inner* SEQUENCE content bytes (stripping the outer 30 82 01 04 TLV)
        //         when verifying messageDigest.  But .NET's ComputeSignature stored
        //         SHA256(full spcDer 264 bytes) because the OCTET STRING wrapper was
        //         present at signing time (and was stripped, leaving 264 raw bytes as the
        //         "content").
        //         Patch: recompute messageDigest = SHA256(spcDer[seqHdrLen..]) and
        //         re-sign signedAttrs with the private key.
        derBytes = PatchMessageDigestAndResign(derBytes, spcDer, cert);

        // AppxSignature.p7x = "PKCX" magic (4 bytes) + DER-encoded ContentInfo
        var result = new byte[4 + derBytes.Length];
        P7xMagic.CopyTo(result.AsSpan(0, 4));
        derBytes.CopyTo(result, 4);
        return result;
    }

    /// <summary>
    /// Patches the <c>SignedData.version</c> INTEGER immediately following the
    /// SignedData SEQUENCE header to <paramref name="version"/>.  Searches for the
    /// pkcs7-signedData OID followed by the [0] EXPLICIT wrapper and the SignedData
    /// SEQUENCE tag to locate the version field robustly, without assuming fixed offsets.
    /// </summary>
    private static void PatchSignedDataVersion(byte[] der, byte version)
    {
        // Locate SignedData SEQUENCE by finding the pkcs7-signedData OID then
        // the two wrappers: [0] EXPLICIT and the SEQUENCE tag.
        // Pattern: A0 ?? ?? 30 ?? ?? 02 01 xx  (the 02 01 xx is the version INTEGER)
        ReadOnlySpan<byte> signedDataOid =
        [
            0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02
        ];
        int oidOff = der.AsSpan().IndexOf(signedDataOid);
        if (oidOff < 0) return; // malformed — should not happen

        // Skip: OID TLV (11 bytes) → A0 tag (1) → A0 len field → 30 tag (1) → 30 len field
        // → 02 tag (1) → 01 len (1) → version byte
        int p = oidOff + signedDataOid.Length; // points to A0
        p++;                                    // skip A0 tag
        SkipBerLenField(der, ref p);            // skip A0 length
        p++;                                    // skip 30 (SignedData SEQUENCE) tag
        SkipBerLenField(der, ref p);            // skip SignedData length
        // Now p points to the version INTEGER tag (02)
        if (p + 2 < der.Length && der[p] == 0x02 && der[p + 1] == 0x01)
            der[p + 2] = version;
    }

    /// <summary>
    /// Inserts the absent <c>NULL</c> (<c>05 00</c>) parameter into <em>every</em>
    /// SHA-256 or RSA <c>AlgorithmIdentifier</c> in the DER that currently lacks it.
    /// signtool always includes the NULL; .NET omits it for both OIDs.
    ///
    /// Occurrences are processed in reverse document order so that each splice
    /// does not shift the positions of earlier occurrences.  For each splice, all
    /// ancestor TLV length fields are incremented via a DER-tree walker.
    /// </summary>
    private static byte[] AddSha256NullParameter(byte[] der)
    {
        // Both SHA-256 (digestAlgorithm) and rsaEncryption (SignerInfo.signatureAlgorithm)
        // require a NULL parameter that .NET's SignedCms omits.
        byte[][] oidList =
        [
            [0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01], // SHA-256
            [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01], // rsaEncryption
        ];

        var insertPositions = new List<int>();
        foreach (var oidBytes in oidList)
        {
            int search = 0;
            while (true)
            {
                int hit = der.AsSpan(search).IndexOf(oidBytes);
                if (hit < 0) break;
                int absHit   = search + hit;
                int afterOid = absHit + oidBytes.Length;
                if (afterOid + 1 >= der.Length || der[afterOid] != 0x05 || der[afterOid + 1] != 0x00)
                    insertPositions.Add(afterOid);    // NULL missing — record insert point
                search = afterOid;
            }
        }

        if (insertPositions.Count == 0) return der;

        // Process in reverse order so earlier positions aren't shifted by later splices
        insertPositions.Sort();
        insertPositions.Reverse();

        var result = der;
        foreach (int insertAt in insertPositions)
        {
            // Insert 05 00 at insertAt
            var next = new byte[result.Length + 2];
            result.AsSpan(0, insertAt).CopyTo(next);
            next[insertAt]     = 0x05;
            next[insertAt + 1] = 0x00;
            result.AsSpan(insertAt).CopyTo(next.AsSpan(insertAt + 2));

            // Increment all ancestor length fields that contain insertAt
            foreach (int lenOff in FindAncestorLenOffsets(next, insertAt))
                IncreaseBerLen(next, lenOff, 2);

            result = next;
        }
        return result;
    }

    /// <summary>
    /// Returns the byte offsets of the length fields of all DER TLVs whose
    /// <em>content</em> spans (i.e., includes) byte position <paramref name="targetPos"/>.
    /// Used to find ancestor containers so their lengths can be updated after a splice.
    /// </summary>
    private static List<int> FindAncestorLenOffsets(byte[] der, int targetPos)
    {
        var result = new List<int>();
        WalkAncestors(der, 0, der.Length, targetPos, result);
        return result;
    }

    private static void WalkAncestors(byte[] der, int start, int end, int target, List<int> result)
    {
        int p = start;
        while (p < end && p < der.Length)
        {
            int tagOff = p;
            byte tag   = der[p++];               // consume tag byte

            if (p >= der.Length) break;

            // Once we've reached or passed the insertion point, no later sibling is an ancestor.
            if (tagOff >= target) break;

            int lenOff = p;
            int first  = der[p];
            int fs, len;
            if (first < 0x80) { fs = 1; len = first; }
            else
            {
                int n = first & 0x7F;
                if (p + n >= der.Length) break;  // malformed: length field overruns array
                fs = 1 + n; len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | der[p + 1 + i];
            }
            p += fs;
            int contentStart = p;
            int contentEnd   = contentStart + len;

            // A TLV is an ancestor of `target` (an insert point) if:
            //   - It is a CONSTRUCTED type (SEQUENCE, SET, context-implicit CONSTRUCTED).
            //     Primitive types (OID=0x06, INTEGER=0x02, etc.) cannot contain children.
            //   - Its content starts strictly before `target` (at least one byte before).
            //   - Its content ends AT OR AFTER `target`, i.e., the insertion falls at the
            //     end of this container's current content or inside it.
            bool isConstructed = (tag & 0x20) != 0;
            if (isConstructed && contentStart < target && contentEnd >= target)
            {
                result.Add(lenOff);
                WalkAncestors(der, contentStart, Math.Min(contentEnd, der.Length), target, result);
                break;  // only one sibling at each level can be an ancestor of `target`
            }

            p = contentEnd;   // skip to next sibling
        }
    }

    private static void SkipBerLenField(byte[] b, ref int p)
    {
        int first = b[p];
        p += first < 0x80 ? 1 : 1 + (first & 0x7F);
    }

    private static void SkipBerTlv(byte[] b, ref int p)
    {
        p++;
        int first = b[p];
        int fs    = first < 0x80 ? 1 : 1 + (first & 0x7F);
        int len   = first < 0x80 ? first : 0;
        if (first >= 0x80) { int n = first & 0x7F; for (int i = 0; i < n; i++) len = (len << 8) | b[p + 1 + i]; }
        p += fs + len;
    }

    private static void IncreaseBerLen(byte[] b, int off, int delta)
    {
        int first = b[off];
        if (first < 0x80) { b[off] = (byte)(first + delta); return; }
        int n   = first & 0x7F;
        int cur = 0;
        for (int i = 0; i < n; i++) cur = (cur << 8) | b[off + 1 + i];
        int newLen = cur + delta;
        b[off] = (byte)(0x80 | n);
        for (int i = n - 1; i >= 0; i--) { b[off + 1 + i] = (byte)(newLen & 0xFF); newLen >>= 8; }
    }

    /// <summary>
    /// Patches the <c>messageDigest</c> signed attribute and the <c>encryptedDigest</c>
    /// (RSA signature over signedAttrs) in an already-computed CMS DER so that they
    /// are consistent with the PKCS#7-compat content-hash formula used by .NET's
    /// <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> and Windows Authenticode
    /// when the <c>eContent</c> is a bare SEQUENCE (no OCTET STRING wrapper).
    ///
    /// <para>Background: after <see cref="RemoveEContentOctetStringWrapper"/> strips the
    /// OCTET STRING wrapper, .NET's <c>GetHashableContentMemory</c> enters PKCS#7-compat
    /// mode (<c>_hasPkcs7Content = true</c>).  It calls <c>AsnDecoder.ReadEncodedValue</c>
    /// on the raw eContent bytes (which are the full spcDer SEQUENCE) and returns only
    /// the <em>value</em> portion — the bytes past the outer <c>30 82 01 xx</c> header —
    /// as the hashable content.  So the expected messageDigest is
    /// <c>SHA256(spcDer[seqHdrLen..])</c>, not <c>SHA256(spcDer)</c>.</para>
    /// </summary>
    private static byte[] PatchMessageDigestAndResign(
        byte[] der, byte[] spcDer, X509Certificate2 cert)
    {
        // 1. Compute correct messageDigest = SHA256(inner SEQUENCE content).
        //    spcDer[0] = 0x30 (SEQUENCE tag).  spcDer[1] encodes the length:
        //      < 0x80  → 1-byte length  → header = 2 bytes
        //      = 0x81  → 2-byte length  → header = 3 bytes
        //      = 0x82  → 3-byte length  → header = 4 bytes  (our case: 30 82 01 04)
        int seqHdrLen = spcDer[1] < 0x80 ? 2 : 2 + (spcDer[1] & 0x7F);
        var newMd     = SHA256.HashData(spcDer.AsSpan(seqHdrLen));

        // 2. Locate the messageDigest OID (1.2.840.113549.1.9.4) in the DER and
        //    overwrite the 32-byte hash value that follows its value SET.
        //    Attribute layout: [30 2F] [06 09 mdOid] [31 22] [04 20] [32 bytes]
        ReadOnlySpan<byte> mdOid =
        [
            0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x04
        ];
        int mdOidPos  = der.AsSpan().IndexOf(mdOid);
        int mdHashOff = mdOidPos + 11   // OID TLV (06 09 + 9 bytes)
                                + 2     // SET-OF header (31 22)
                                + 2;    // OCTET STRING header (04 20)
        newMd.CopyTo(der.AsSpan(mdHashOff, 32));

        // 3. Locate signedAttrs [0] IMPLICIT (tag A0, length 0x7C = 124 bytes),
        //    preceded immediately by the first attribute: SPC_SP_OPUS_INFO (30 10).
        //    We search for the 4-byte sequence A0 7C 30 10 to avoid false matches.
        ReadOnlySpan<byte> saPattern = [0xA0, 0x7C, 0x30, 0x10];
        int saPos     = der.AsSpan().IndexOf(saPattern);
        int saFullLen = 2 + der[saPos + 1];   // tag(1) + len(1) + content(0x7C) = 126 bytes

        // 4. Build the RSA signing input: signedAttrs with the implicit-[0] tag
        //    replaced by the SET-OF tag (0x31), as required by RFC 5652 §5.4.
        var saBytes = der.AsSpan(saPos, saFullLen).ToArray();
        saBytes[0]  = 0x31;                   // A0 → 31 (SET)
        var saHash  = SHA256.HashData(saBytes);

        // 5. Sign the hash with the signer's private key (PKCS#1 v1.5, SHA-256).
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new CryptographicException(
                "The signing certificate does not have an accessible private key.");
        var rsaSig = rsa.SignHash(saHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // 6. Find the encryptedDigest: the last OCTET STRING of length 256 in the DER
        //    (04 82 01 00).  Overwrite its 256 value bytes in-place.
        ReadOnlySpan<byte> edHdr = [0x04, 0x82, 0x01, 0x00];
        int edPos = -1;
        for (int i = der.Length - 4; i >= 0; i--)
        {
            if (der.AsSpan(i, 4).SequenceEqual(edHdr)) { edPos = i; break; }
        }
        if (edPos < 0)
            throw new CryptographicException("encryptedDigest field not found in DER.");

        rsaSig.CopyTo(der.AsSpan(edPos + 4, 256));
        return der;
    }

    /// <summary>
    /// Removes the <c>OCTET STRING</c> tag+length that .NET's <see cref="SignedCms"/>
    /// inserts around the <c>eContent</c> value.  The MSIX SIP expects the
    /// <c>SpcIndirectDataContent</c> DER to appear directly inside the
    /// <c>[0] EXPLICIT eContent</c>, without any wrapping OCTET STRING.
    /// </summary>
    private static byte[] RemoveEContentOctetStringWrapper(byte[] der)
    {
        // ---------- helpers -----------------------------------------------
        static (int len, int fieldSize) ReadLen(byte[] b, int off)
        {
            int first = b[off];
            if (first < 0x80) return (first, 1);
            int n = first & 0x7F;
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | b[off + 1 + i];
            return (v, 1 + n);
        }

        static void DecreaseLen(byte[] b, int off, int delta)
        {
            var (cur, fs) = ReadLen(b, off);
            int newLen = cur - delta;
            if (fs == 1) { b[off] = (byte)newLen; return; }
            // Multi-byte: write back in the same number of bytes
            int n = fs - 1;
            b[off] = (byte)(0x80 | n);
            int tmp = newLen;
            for (int i = n - 1; i >= 0; i--) { b[off + 1 + i] = (byte)(tmp & 0xFF); tmp >>= 8; }
        }

        static void SkipLenField(byte[] b, ref int p) { var (_, fs) = ReadLen(b, p); p += fs; }
        static void SkipTlv(byte[] b, ref int p) { p++; var (l, fs) = ReadLen(b, p); p += fs + l; }

        // ---------- locate the OCTET STRING header -------------------------
        // The SPC_INDIRECT_DATA_OBJID immediately precedes the [0] EXPLICIT eContent.
        ReadOnlySpan<byte> sipDataOid =
        [
            0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x01, 0x04
        ];
        int oidOff = der.AsSpan().IndexOf(sipDataOid);
        int afterOid = oidOff + sipDataOid.Length;  // points to A0 ([0] EXPLICIT)

        int a0LenOff = afterOid + 1;
        var (_, a0Lfs) = ReadLen(der, a0LenOff);
        int osTagOff = a0LenOff + a0Lfs;          // should be 0x04 (OCTET STRING)
        int osLenOff = osTagOff + 1;
        var (_, osLfs) = ReadLen(der, osLenOff);
        int toRemove = 1 + osLfs;                  // 04 + length-field bytes

        // ---------- collect the 5 ancestor length-field offsets ------------
        // Walk forward from byte 0 to find each enclosing SEQUENCE/[0] header.
        int p = 0;
        int[] lenOffs = new int[5];

        p++;           lenOffs[0] = p; SkipLenField(der, ref p);  // ContentInfo SEQUENCE
        SkipTlv(der, ref p);                                        // pkcs7-signedData OID
        p++;           lenOffs[1] = p; SkipLenField(der, ref p);  // [0] EXPLICIT
        p++;           lenOffs[2] = p; SkipLenField(der, ref p);  // SignedData SEQUENCE
        SkipTlv(der, ref p);                                        // version INTEGER
        SkipTlv(der, ref p);                                        // digestAlgorithms SET
        p++;           lenOffs[3] = p; SkipLenField(der, ref p);  // encapContentInfo SEQUENCE
        SkipTlv(der, ref p);                                        // eContentType OID
        p++;           lenOffs[4] = p;                              // [0] EXPLICIT eContent

        // ---------- fix lengths and splice --------------------------------
        var result = (byte[])der.Clone();
        foreach (int lo in lenOffs)
            DecreaseLen(result, lo, toRemove);

        var final = new byte[result.Length - toRemove];
        result.AsSpan(0, osTagOff).CopyTo(final);
        result.AsSpan(osTagOff + toRemove).CopyTo(final.AsSpan(osTagOff));
        return final;
    }

    private static byte[] ReadEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidOperationException($"'{entryName}' not found in package.");
        using var stream = entry.Open();
        using var ms     = new MemoryStream((int)entry.Length);
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[]? TryReadEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms     = new MemoryStream((int)entry.Length);
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Copies all entries from <paramref name="sourceMsix"/> to
    /// <paramref name="destMsix"/> using the makeappx-compatible ZIP format,
    /// replacing or adding <paramref name="entryName"/> with <paramref name="newContent"/>.
    /// AppxSignature.p7x is always written last.
    /// </summary>
    /// <summary>
    /// Adds a helper that injects the AppxSignature.p7x override into a
    /// <c>[Content_Types].xml</c> byte array so the APPX SIP can locate the
    /// signature file.  The override is inserted immediately before the closing
    /// <c>&lt;/Types&gt;</c> tag.
    /// </summary>
    internal static byte[] InjectSignatureContentType(byte[] contentTypesBytes)
    {
        const string Marker   = "</Types>";
        const string Override = "<Override PartName=\"/AppxSignature.p7x\" ContentType=\"application/vnd.ms-appx.signature\"/>"; 
        var xml = Encoding.UTF8.GetString(contentTypesBytes);
        var idx = xml.LastIndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0)
            return contentTypesBytes; // malformed – leave unchanged
        // Only inject if not already present
        if (xml.Contains("/AppxSignature.p7x", StringComparison.Ordinal))
            return contentTypesBytes;
        xml = xml.Insert(idx, Override);
        return Encoding.UTF8.GetBytes(xml);
    }

    private static void CopyZipReplaceEntry(
        string sourceMsix,
        string destMsix,
        string entryName,
        byte[] newContent,
        string? inPlaceEntry   = null,
        byte[]? inPlaceContent = null)
    {
        // Read all existing entries (excluding the one appended last) into memory.
        // If inPlaceEntry is specified its content is swapped out while keeping its
        // original position in the ZIP.
        var entries = new List<(string name, byte[] data)>();
        using (var src = ZipFile.OpenRead(sourceMsix))
        {
            foreach (var entry in src.Entries)
            {
                if (entry.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                    continue;
                byte[] data;
                if (inPlaceEntry != null &&
                    entry.FullName.Equals(inPlaceEntry, StringComparison.OrdinalIgnoreCase))
                {
                    data = inPlaceContent!;
                }
                else
                {
                    using var ms = new MemoryStream();
                    using var es = entry.Open();
                    es.CopyTo(ms);
                    data = ms.ToArray();
                }
                entries.Add((entry.FullName, data));
            }
        }

        // Append the replacement entry last (AppxSignature.p7x must be the last entry).
        entries.Add((entryName, newContent));

        // Write using makeappx-compatible format (Zip64, data descriptors, version=45).
        var now     = DateTime.Now;
        ushort dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort dosDate = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);

        using var fs = new FileStream(
            destMsix, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        var cdEntries = new List<(string name, uint crc32, uint csz, uint usz, ushort time, ushort date, uint lfhOff, bool isStd)>();
        foreach (var (name, data) in entries)
        {
            // [Content_Types].xml and AppxSignature.p7x use standard format (version=20, no
            // data descriptor) — the APPX package reader reads these files using LFH sizes directly.
            if (name.Equals("AppxSignature.p7x", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                MsixPacker.WriteStandardZipEntry(bw, name, data, dosTime, dosDate, cdEntries);
            else
                MsixPacker.WriteAppxZipEntry(bw, name, data, dosTime, dosDate, cdEntries);
        }

        long cdStart = fs.Position;
        foreach (var cd in cdEntries)
            MsixPacker.WriteAppxCdEntry(bw, cd);
        long cdSize = fs.Position - cdStart;

        long eocd64Off = fs.Position;
        MsixPacker.WriteZip64Eocd(bw, cdEntries.Count, cdSize, cdStart);
        MsixPacker.WriteZip64EocdLocator(bw, eocd64Off);
        MsixPacker.WriteStdEocd(bw);
    }
}
