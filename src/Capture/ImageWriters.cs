using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZDepth
{
    /// <summary>
    /// Minimal writers for single-channel images: greyscale PNG (8 or 16 bit) and a float
    /// OpenEXR with one Z channel. No dependencies, the same on Mac and Windows.
    /// </summary>
    internal static class ImageWriters
    {
        public static void WritePng8(string path, int width, int height, byte[] pixels)
        {
            WritePng(path, width, height, 8, pixels);
        }

        public static void WritePng16(string path, int width, int height, ushort[] pixels)
        {
            var bytes = new byte[pixels.Length * 2];
            for (int i = 0; i < pixels.Length; i++)
            {
                bytes[2 * i] = (byte)(pixels[i] >> 8);   // PNG samples are big-endian
                bytes[2 * i + 1] = (byte)pixels[i];
            }
            WritePng(path, width, height, 16, bytes);
        }

        /// <summary>
        /// Scanline OpenEXR, no compression, one FLOAT channel named "Z", rows top to bottom.
        /// </summary>
        public static void WriteExr(string path, int width, int height, float[] z)
        {
            using (var stream = File.Create(path))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(20000630);  // magic
                w.Write(2);         // version 2, single-part scanline

                Attribute(w, "channels", "chlist", 2 + 16 + 1, () =>
                {
                    w.Write(Encoding.ASCII.GetBytes("Z\0"));
                    w.Write(2);           // FLOAT
                    w.Write(0);           // pLinear + 3 reserved bytes
                    w.Write(1);           // xSampling
                    w.Write(1);           // ySampling
                    w.Write((byte)0);     // end of channel list
                });
                Attribute(w, "compression", "compression", 1, () => w.Write((byte)0));
                Attribute(w, "dataWindow", "box2i", 16, () => { w.Write(0); w.Write(0); w.Write(width - 1); w.Write(height - 1); });
                Attribute(w, "displayWindow", "box2i", 16, () => { w.Write(0); w.Write(0); w.Write(width - 1); w.Write(height - 1); });
                Attribute(w, "lineOrder", "lineOrder", 1, () => w.Write((byte)0));
                Attribute(w, "pixelAspectRatio", "float", 4, () => w.Write(1f));
                Attribute(w, "screenWindowCenter", "v2f", 8, () => { w.Write(0f); w.Write(0f); });
                Attribute(w, "screenWindowWidth", "float", 4, () => w.Write(1f));
                w.Write((byte)0);   // end of header

                // Offset table: one block per scanline, each block = y, size, data.
                long tableStart = stream.Position;
                long rowBytes = 4L * width;
                long first = tableStart + 8L * height;
                for (int y = 0; y < height; y++)
                    w.Write(first + y * (8 + rowBytes));
                for (int y = 0; y < height; y++)
                {
                    w.Write(y);
                    w.Write((int)rowBytes);
                    for (int x = 0; x < width; x++)
                        w.Write(z[y * width + x]);
                }
            }
        }

        static void Attribute(BinaryWriter w, string name, string type, int size, Action value)
        {
            w.Write(Encoding.ASCII.GetBytes(name + "\0" + type + "\0"));
            w.Write(size);
            value();
        }

        static void WritePng(string path, int width, int height, int bitDepth, byte[] samples)
        {
            int rowBytes = width * bitDepth / 8;
            byte[] compressed;
            using (var zlib = new MemoryStream())
            {
                zlib.WriteByte(0x78);   // zlib header: deflate, 32K window
                zlib.WriteByte(0x01);
                uint a = 1, b = 0;      // Adler-32 of the uncompressed data
                using (var deflate = new DeflateStream(zlib, CompressionLevel.Optimal, true))
                {
                    var row = new byte[rowBytes + 1];
                    row[0] = 2;         // filter "Up": depth changes slowly from row to row
                    for (int y = 0; y < height; y++)
                    {
                        int o = y * rowBytes;
                        for (int i = 0; i < rowBytes; i++)
                            row[i + 1] = (byte)(samples[o + i] - (y > 0 ? samples[o - rowBytes + i] : 0));
                        deflate.Write(row, 0, row.Length);
                        foreach (var v in row)
                        {
                            a = (a + v) % 65521;
                            b = (b + a) % 65521;
                        }
                    }
                }
                WriteBigEndian(zlib, (b << 16) | a);
                compressed = zlib.ToArray();
            }

            using (var stream = File.Create(path))
            {
                stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new MemoryStream();
                WriteBigEndian(header, (uint)width);
                WriteBigEndian(header, (uint)height);
                header.WriteByte((byte)bitDepth);
                header.WriteByte(0);    // greyscale
                header.WriteByte(0);    // deflate
                header.WriteByte(0);    // adaptive filtering
                header.WriteByte(0);    // no interlace
                Chunk(stream, "IHDR", header.ToArray());
                Chunk(stream, "IDAT", compressed);
                Chunk(stream, "IEND", new byte[0]);
            }
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            WriteBigEndian(s, (uint)data.Length);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(Crc32(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            WriteBigEndian(s, crc);
        }

        static void WriteBigEndian(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        static uint Crc32(uint crc, byte[] data)
        {
            foreach (var b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
