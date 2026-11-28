using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace KTLanDrop
{
    public static class ComicHelper
    {
        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
        };

        private static readonly HashSet<string> ArchiveExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".cbz"
        };

        public static bool IsImageFile(string name)
        {
            return ImageExts.Contains(Path.GetExtension(name ?? ""));
        }

        public static bool IsComicArchive(string name)
        {
            return ArchiveExts.Contains(Path.GetExtension(name ?? ""));
        }

        public static int NaturalCompare(string a, string b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int ia = 0, ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                char ca = a[ia], cb = b[ib];
                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    while (ia < a.Length && a[ia] == '0') ia++;
                    while (ib < b.Length && b[ib] == '0') ib++;
                    int sa = ia, sb = ib;
                    while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                    while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                    int lenA = ia - sa, lenB = ib - sb;
                    if (lenA != lenB) return lenA.CompareTo(lenB);
                    for (int k = 0; k < lenA; k++)
                    {
                        if (a[sa + k] != b[sb + k]) return a[sa + k].CompareTo(b[sb + k]);
                    }
                    if (sa != sb) return sa.CompareTo(sb);
                }
                else
                {
                    char ua = char.ToUpperInvariant(ca);
                    char ub = char.ToUpperInvariant(cb);
                    if (ua != ub) return ua.CompareTo(ub);
                    ia++;
                    ib++;
                }
            }
            return a.Length.CompareTo(b.Length);
        }

        private class ChapterItem
        {
            public string Name;
            public string RelPath;
            public string Type; // "zip" or "dir"
        }

        private static bool HasDirectImages(string dirPath)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dirPath))
                {
                    if (IsImageFile(f)) return true;
                }
            }
            catch { }
            return false;
        }

        private static List<ChapterItem> ScanChaptersInDir(string shareRoot, string parentDir)
        {
            List<ChapterItem> list = new List<ChapterItem>();
            try
            {
                DirectoryInfo di = new DirectoryInfo(parentDir);
                foreach (DirectoryInfo sub in di.GetDirectories())
                {
                    if ((sub.Attributes & FileAttributes.Hidden) != 0 || sub.Name.StartsWith(".")) continue;
                    if (HasDirectImages(sub.FullName))
                    {
                        string rel = sub.FullName.Substring(shareRoot.Length).TrimStart('\\').Replace('\\', '/');
                        list.Add(new ChapterItem { Name = sub.Name, RelPath = rel, Type = "dir" });
                    }
                }
                foreach (FileInfo fi in di.GetFiles())
                {
                    if ((fi.Attributes & FileAttributes.Hidden) != 0 || fi.Name.StartsWith(".")) continue;
                    if (IsComicArchive(fi.Name))
                    {
                        string rel = fi.FullName.Substring(shareRoot.Length).TrimStart('\\').Replace('\\', '/');
                        list.Add(new ChapterItem { Name = Path.GetFileNameWithoutExtension(fi.Name), RelPath = rel, Type = "zip" });
                    }
                }
            }
            catch { }
            list.Sort((x, y) => NaturalCompare(x.Name, y.Name));
            return list;
        }

        public static string GetComicInfoJson(string shareRoot, string relPath)
        {
            string root = Path.GetFullPath(shareRoot).TrimEnd('\\');
            string cleanRel = (relPath ?? "").Replace('/', '\\').TrimStart('\\');
            string full = string.IsNullOrEmpty(cleanRel) ? root : Path.GetFullPath(Path.Combine(root, cleanRel));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return "{\"error\":\"Forbidden\"}";

            bool isFile = File.Exists(full);
            bool isDir = Directory.Exists(full);
            if (!isFile && !isDir)
                return "{\"error\":\"Not found\"}";

            string seriesDir;
            string activeRel = cleanRel.Replace('\\', '/');

            if (isDir && !HasDirectImages(full))
            {
                // User opened a series folder containing chapters (.zip or image subfolders)
                List<ChapterItem> childChapters = ScanChaptersInDir(root, full);
                if (childChapters.Count > 0)
                {
                    seriesDir = full;
                    activeRel = childChapters[0].RelPath;
                    full = Path.GetFullPath(Path.Combine(root, activeRel.Replace('/', '\\')));
                    isFile = File.Exists(full);
                    isDir = Directory.Exists(full);
                }
                else
                {
                    seriesDir = full;
                }
            }
            else
            {
                seriesDir = Path.GetDirectoryName(full) ?? root;
                if (seriesDir.Length < root.Length) seriesDir = root;
            }

            List<ChapterItem> chapters = ScanChaptersInDir(root, seriesDir);
            if (chapters.Count == 0 && (isFile || isDir))
            {
                chapters.Add(new ChapterItem
                {
                    Name = isFile ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full),
                    RelPath = activeRel,
                    Type = isFile ? "zip" : "dir"
                });
            }

            List<string> pages = new List<string>();
            if (isFile && IsComicArchive(full))
            {
                try
                {
                    using (ZipArchive zip = ZipFile.OpenRead(full))
                    {
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name) || entry.Length <= 0) continue;
                            if (IsImageFile(entry.Name))
                                pages.Add(entry.FullName);
                        }
                    }
                    pages.Sort(NaturalCompare);
                }
                catch { }
            }
            else if (isDir)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(full))
                    {
                        if (IsImageFile(f)) pages.Add(Path.GetFileName(f));
                    }
                    pages.Sort(NaturalCompare);
                }
                catch { }
            }

            string seriesRel = seriesDir.Length <= root.Length ? "/" : seriesDir.Substring(root.Length + 1).Replace('\\', '/');
            string currentName = isFile ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full);

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"series\":\"").Append(JsonEscape(seriesRel))
              .Append("\",\"current\":\"").Append(JsonEscape(activeRel))
              .Append("\",\"currentName\":\"").Append(JsonEscape(currentName))
              .Append("\",\"chapters\":[");
            for (int i = 0; i < chapters.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(JsonEscape(chapters[i].Name))
                  .Append("\",\"path\":\"").Append(JsonEscape(chapters[i].RelPath))
                  .Append("\",\"type\":\"").Append(chapters[i].Type).Append("\"}");
            }
            sb.Append("],\"pages\":[");
            for (int i = 0; i < pages.Count; i++)
            {
                if (i > 0) sb.Append(",");
                string pageUrl = "/api/comic/page?path=" + Uri.EscapeDataString(activeRel) + "&entry=" + Uri.EscapeDataString(pages[i]);
                sb.Append("\"").Append(JsonEscape(pageUrl)).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static void ServeComicPage(NetworkStream stream, string shareRoot, string relPath, string entry, bool headOnly)
        {
            string root = Path.GetFullPath(shareRoot).TrimEnd('\\');
            string cleanRel = (relPath ?? "").Replace('/', '\\').TrimStart('\\');
            string full = string.IsNullOrEmpty(cleanRel) ? root : Path.GetFullPath(Path.Combine(root, cleanRel));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry))
            {
                Send404(stream);
                return;
            }

            if (File.Exists(full) && IsComicArchive(full))
            {
                using (ZipArchive zip = ZipFile.OpenRead(full))
                {
                    ZipArchiveEntry ze = zip.GetEntry(entry);
                    if (ze == null) { Send404(stream); return; }
                    string ctype = GetImageContentType(ze.Name);
                    string hdr = string.Format("HTTP/1.1 200 OK\r\nContent-Type: {0}\r\nContent-Length: {1}\r\nCache-Control: public, max-age=3600\r\nConnection: close\r\n\r\n", ctype, ze.Length);
                    byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
                    stream.Write(hdrBytes, 0, hdrBytes.Length);
                    if (!headOnly)
                    {
                        using (Stream zs = ze.Open())
                        {
                            byte[] buf = new byte[65536];
                            int r;
                            while ((r = zs.Read(buf, 0, buf.Length)) > 0)
                                stream.Write(buf, 0, r);
                        }
                    }
                    stream.Flush();
                }
            }
            else if (Directory.Exists(full))
            {
                string safeFile = Path.GetFileName(entry);
                string imgFull = Path.GetFullPath(Path.Combine(full, safeFile));
                if (!imgFull.StartsWith(full, StringComparison.OrdinalIgnoreCase) || !File.Exists(imgFull))
                {
                    Send404(stream);
                    return;
                }
                FileInfo fi = new FileInfo(imgFull);
                string ctype = GetImageContentType(fi.Name);
                string hdr = string.Format("HTTP/1.1 200 OK\r\nContent-Type: {0}\r\nContent-Length: {1}\r\nCache-Control: public, max-age=3600\r\nConnection: close\r\n\r\n", ctype, fi.Length);
                byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
                stream.Write(hdrBytes, 0, hdrBytes.Length);
                if (!headOnly)
                {
                    using (FileStream fs = File.OpenRead(imgFull))
                    {
                        byte[] buf = new byte[65536];
                        int r;
                        while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                            stream.Write(buf, 0, r);
                    }
                }
                stream.Flush();
            }
            else
            {
                Send404(stream);
            }
        }

        private static string GetImageContentType(string name)
        {
            switch (Path.GetExtension(name ?? "").ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }

        private static void Send404(NetworkStream stream)
        {
            byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nNot Found");
            stream.Write(b, 0, b.Length);
            stream.Flush();
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
