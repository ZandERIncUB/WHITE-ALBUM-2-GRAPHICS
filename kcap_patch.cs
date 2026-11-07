using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Wa2Graphics {
    public static class KcapPatch {
        private sealed class Entry {
            public long IndexPosition;
            public uint Flag;
            public uint Offset;
            public uint Size;
        }

        private static string Hex(byte[] bytes) {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) text.Append(value.ToString("x2"));
            return text.ToString();
        }

        private static Dictionary<string, Entry> ReadIndex(FileStream stream, int expectedCount,
                                                             string expectedNameHash) {
            BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, true);
            stream.Position = 0;
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "KCAP")
                throw new InvalidDataException("Not a KCAP archive");
            stream.Position = 12;
            uint count = reader.ReadUInt32();
            if (count != expectedCount)
                throw new InvalidDataException("KCAP entry count differs from supported game");
            Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            Encoding sjis = Encoding.GetEncoding(932);
            using (SHA256 digest = SHA256.Create()) {
                for (int i = 0; i < count; i++) {
                    long position = 16L + i * 44L;
                    stream.Position = position;
                    uint flag = reader.ReadUInt32();
                    byte[] rawName = reader.ReadBytes(24);
                    if (rawName.Length != 24) throw new EndOfStreamException("KCAP index truncated");
                    digest.TransformBlock(rawName, 0, rawName.Length, rawName, 0);
                    int end = Array.IndexOf(rawName, (byte)0);
                    if (end < 0) end = rawName.Length;
                    string name = sjis.GetString(rawName, 0, end);
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    uint offset = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    if (size != 0 && (flag > 1 || (ulong)offset + size > (ulong)stream.Length))
                        throw new InvalidDataException("Invalid KCAP entry: " + name);
                    if (entries.ContainsKey(name)) throw new InvalidDataException("Duplicate KCAP entry: " + name);
                    entries.Add(name, new Entry { IndexPosition = position, Flag = flag,
                                                  Offset = offset, Size = size });
                }
                digest.TransformFinalBlock(new byte[0], 0, 0);
                if (!String.Equals(Hex(digest.Hash), expectedNameHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("KCAP name table differs from supported game");
            }
            return entries;
        }

        private static string HashRange(FileStream stream, long offset, long size) {
            byte[] buffer = new byte[4 * 1024 * 1024];
            stream.Position = offset;
            using (SHA256 digest = SHA256.Create()) {
                long remaining = size;
                while (remaining > 0) {
                    int requested = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, requested);
                    if (read == 0) throw new EndOfStreamException("KCAP entry truncated");
                    digest.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                digest.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(digest.Hash);
            }
        }

        private static void CheckArrays(string[] names, string[] members, string[] hashes, long[] sizes) {
            if (names.Length != members.Length || names.Length != hashes.Length || names.Length != sizes.Length)
                throw new ArgumentException("Graphics manifest arrays differ in length");
        }

        public static int Check(string source, string zipPath, int count, string nameHash,
                                string[] names, string[] members, string[] hashes, long[] sizes) {
            CheckArrays(names, members, hashes, sizes);
            using (FileStream archive = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive assets = ZipFile.OpenRead(zipPath)) {
                Dictionary<string, Entry> entries = ReadIndex(archive, count, nameHash);
                int pending = 0;
                for (int i = 0; i < names.Length; i++) {
                    Entry entry;
                    if (!entries.TryGetValue(names[i], out entry))
                        throw new InvalidDataException("Missing KCAP entry: " + names[i]);
                    ZipArchiveEntry asset = assets.GetEntry(members[i]);
                    if (asset == null || asset.Length != sizes[i])
                        throw new InvalidDataException("Missing or sized incorrectly: " + members[i]);
                    if (entry.Flag != 0 || entry.Size != sizes[i] ||
                        !String.Equals(HashRange(archive, entry.Offset, entry.Size), hashes[i], StringComparison.OrdinalIgnoreCase))
                        pending++;
                }
                return pending;
            }
        }

        public static int Stage(string source, string stage, string zipPath, int count, string nameHash,
                                string[] names, string[] members, string[] hashes, long[] sizes) {
            CheckArrays(names, members, hashes, sizes);
            if (File.Exists(stage)) throw new IOException("Staging file already exists: " + stage);
            File.Copy(source, stage);
            File.SetAttributes(stage, File.GetAttributes(stage) & ~FileAttributes.ReadOnly);
            int changed = 0;
            try {
                using (FileStream archive = File.Open(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive assets = ZipFile.OpenRead(zipPath)) {
                    Dictionary<string, Entry> entries = ReadIndex(archive, count, nameHash);
                    BinaryWriter writer = new BinaryWriter(archive, Encoding.ASCII, true);
                    byte[] buffer = new byte[4 * 1024 * 1024];
                    for (int i = 0; i < names.Length; i++) {
                        Entry entry;
                        if (!entries.TryGetValue(names[i], out entry))
                            throw new InvalidDataException("Missing KCAP entry: " + names[i]);
                        if (entry.Flag == 0 && entry.Size == sizes[i] &&
                            String.Equals(HashRange(archive, entry.Offset, entry.Size), hashes[i], StringComparison.OrdinalIgnoreCase))
                            continue;
                        ZipArchiveEntry asset = assets.GetEntry(members[i]);
                        if (asset == null || asset.Length != sizes[i])
                            throw new InvalidDataException("Missing or sized incorrectly: " + members[i]);
                        long offset = archive.Length;
                        if (offset > UInt32.MaxValue || sizes[i] > UInt32.MaxValue || offset + sizes[i] > UInt32.MaxValue)
                            throw new InvalidDataException("KCAP archive would exceed its 32-bit offset limit");
                        archive.Position = offset;
                        long written = 0;
                        string actualHash;
                        using (Stream input = asset.Open())
                        using (SHA256 digest = SHA256.Create()) {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0) {
                                archive.Write(buffer, 0, read);
                                digest.TransformBlock(buffer, 0, read, buffer, 0);
                                written += read;
                            }
                            digest.TransformFinalBlock(new byte[0], 0, 0);
                            actualHash = Hex(digest.Hash);
                        }
                        if (written != sizes[i] || !String.Equals(actualHash, hashes[i], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Graphics payload hash failed: " + members[i]);
                        archive.Position = entry.IndexPosition;
                        writer.Write((uint)0);
                        archive.Position = entry.IndexPosition + 36;
                        writer.Write((uint)offset);
                        writer.Write((uint)written);
                        changed++;
                    }
                    archive.Flush(true);
                    entries = ReadIndex(archive, count, nameHash);
                    for (int i = 0; i < names.Length; i++) {
                        Entry entry = entries[names[i]];
                        if (entry.Flag != 0 || entry.Size != sizes[i] ||
                            !String.Equals(HashRange(archive, entry.Offset, entry.Size), hashes[i], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Staged KCAP verification failed: " + names[i]);
                    }
                }
                return changed;
            } catch {
                if (File.Exists(stage)) File.Delete(stage);
                throw;
            }
        }
    }
}
