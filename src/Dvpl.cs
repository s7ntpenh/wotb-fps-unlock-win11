// Dvpl.cs - unpack/pack/search DAVA Engine .dvpl containers (World of Tanks Blitz).
//
// DVPL container layout:
//   [ payload ][ 20-byte footer ]
//   footer: uint32 srcSize | uint32 compSize | uint32 crc32(payload) | uint32 type | "DVPL"
//   type: 0 = stored, 1 = LZ4, 2 = LZ4HC
//
// Packing always emits type 1 with a literals-only LZ4 block. That is a valid LZ4
// block the engine decodes normally; it costs a few bytes per file and saves us
// from shipping an LZ4 encoder.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class Dvpl
{
    const uint Marker = 0x4C505644; // "DVPL" little-endian

    // ---------- CRC32 ----------
    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static uint Crc32(byte[] buf)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < buf.Length; i++)
            c = CrcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ---------- LZ4 block ----------
    static byte[] Lz4Decompress(byte[] src, int outLen)
    {
        var dst = new byte[outLen];
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                int b;
                do { b = src[s++]; litLen += b; } while (b == 255);
            }
            Buffer.BlockCopy(src, s, dst, d, litLen);
            s += litLen; d += litLen;
            if (s >= src.Length) break;

            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                int b;
                do { b = src[s++]; matchLen += b; } while (b == 255);
            }
            matchLen += 4;
            int m = d - offset;
            for (int i = 0; i < matchLen; i++) dst[d++] = dst[m++];
        }
        if (d != outLen)
            throw new InvalidDataException("LZ4 size mismatch: got " + d + ", expected " + outLen);
        return dst;
    }

    // Literals-only LZ4 block: one token, no match sequence.
    static byte[] Lz4StoreAsBlock(byte[] src)
    {
        var ms = new MemoryStream();
        int n = src.Length;
        if (n < 15)
        {
            ms.WriteByte((byte)(n << 4));
        }
        else
        {
            ms.WriteByte(0xF0);
            int rem = n - 15;
            while (rem >= 255) { ms.WriteByte(255); rem -= 255; }
            ms.WriteByte((byte)rem);
        }
        ms.Write(src, 0, n);
        return ms.ToArray();
    }

    // ---------- container ----------
    struct Footer
    {
        public uint SrcSize, CompSize, Crc, Type;
    }

    static Footer ReadFooter(byte[] raw)
    {
        if (raw.Length < 20) throw new InvalidDataException("file too small to be DVPL");
        int o = raw.Length - 20;
        if (BitConverter.ToUInt32(raw, o + 16) != Marker)
            throw new InvalidDataException("missing DVPL marker");
        return new Footer
        {
            SrcSize = BitConverter.ToUInt32(raw, o + 0),
            CompSize = BitConverter.ToUInt32(raw, o + 4),
            Crc = BitConverter.ToUInt32(raw, o + 8),
            Type = BitConverter.ToUInt32(raw, o + 12),
        };
    }

    public static byte[] Decode(byte[] raw)
    {
        var f = ReadFooter(raw);
        var payload = new byte[f.CompSize];
        Buffer.BlockCopy(raw, 0, payload, 0, (int)f.CompSize);
        if (Crc32(payload) != f.Crc)
            throw new InvalidDataException("CRC mismatch - file is corrupt");
        if (f.Type == 0) return payload;
        return Lz4Decompress(payload, (int)f.SrcSize);
    }

    public static byte[] Encode(byte[] plain)
    {
        byte[] payload = Lz4StoreAsBlock(plain);
        var outBuf = new byte[payload.Length + 20];
        Buffer.BlockCopy(payload, 0, outBuf, 0, payload.Length);
        int o = payload.Length;
        Buffer.BlockCopy(BitConverter.GetBytes((uint)plain.Length), 0, outBuf, o + 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)payload.Length), 0, outBuf, o + 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(Crc32(payload)), 0, outBuf, o + 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, outBuf, o + 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(Marker), 0, outBuf, o + 16, 4);
        return outBuf;
    }

    // ---------- CLI ----------
    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "unpack": return CmdUnpack(args);
                case "pack": return CmdPack(args);
                case "info": return CmdInfo(args);
                case "grep": return CmdGrep(args);
                default: Usage(); return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    static void Usage()
    {
        Console.WriteLine(@"dvpl - DAVA .dvpl tool

  dvpl unpack <file.dvpl> [out]     decode one file (default: drop .dvpl suffix)
  dvpl pack   <file> [out.dvpl]     encode one file
  dvpl info   <file.dvpl>           print footer fields
  dvpl grep   <dir> <text>          decode every .dvpl under <dir>, print matches
");
    }

    static int CmdUnpack(string[] a)
    {
        string inPath = a[1];
        string outPath = a.Length > 2 ? a[2]
            : (inPath.EndsWith(".dvpl", StringComparison.OrdinalIgnoreCase)
                ? inPath.Substring(0, inPath.Length - 5) : inPath + ".out");
        File.WriteAllBytes(outPath, Decode(File.ReadAllBytes(inPath)));
        Console.WriteLine("unpacked -> " + outPath);
        return 0;
    }

    static int CmdPack(string[] a)
    {
        string inPath = a[1];
        string outPath = a.Length > 2 ? a[2] : inPath + ".dvpl";
        File.WriteAllBytes(outPath, Encode(File.ReadAllBytes(inPath)));
        Console.WriteLine("packed -> " + outPath);
        return 0;
    }

    static int CmdInfo(string[] a)
    {
        var raw = File.ReadAllBytes(a[1]);
        var f = ReadFooter(raw);
        string[] kinds = { "stored", "lz4", "lz4hc" };
        Console.WriteLine("src={0} comp={1} crc=0x{2:x8} type={3} ({4})",
            f.SrcSize, f.CompSize, f.Crc, f.Type,
            f.Type < 3 ? kinds[f.Type] : "unknown");
        return 0;
    }

    static int CmdGrep(string[] a)
    {
        string dir = a[1], needle = a[2];
        int hits = 0;
        foreach (var path in Directory.GetFiles(dir, "*.dvpl", SearchOption.AllDirectories))
        {
            byte[] plain;
            try { plain = Decode(File.ReadAllBytes(path)); }
            catch { continue; }
            string text = Encoding.UTF8.GetString(plain);
            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            hits++;
            Console.WriteLine("=== " + path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 8); j++)
                    Console.WriteLine("  {0,5}: {1}", j + 1, lines[j]);
                Console.WriteLine("  ---");
            }
        }
        Console.WriteLine("files matched: " + hits);
        return 0;
    }
}
