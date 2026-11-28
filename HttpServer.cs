using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KTLanDrop
{
    public class ChatMessage
    {
        public long Id { get; set; }
        public string Sender { get; set; }
        public string Text { get; set; }
        public string Time { get; set; }
    }

    public class HttpServer : IDisposable
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private readonly object _msgLock = new object();
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private long _nextMsgId = 1;

        public string ShareRoot { get; set; }
        public string ReceiveRoot { get; set; }
        public int Port { get; private set; }
        public bool IsRunning { get { return _running; } }

        public event Action<ChatMessage> OnMessageAdded;
        public event Action<string> OnFileReceived;

        public HttpServer(string shareRoot, string receiveRoot, int port)
        {
            ShareRoot = Path.GetFullPath(shareRoot).TrimEnd('\\');
            ReceiveRoot = Path.GetFullPath(receiveRoot).TrimEnd('\\');
            Port = port;
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        public ChatMessage AddMessage(string sender, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            ChatMessage msg;
            lock (_msgLock)
            {
                msg = new ChatMessage
                {
                    Id = _nextMsgId++,
                    Sender = sender,
                    Text = text,
                    Time = DateTime.Now.ToString("HH:mm:ss")
                };
                _messages.Add(msg);
                if (_messages.Count > 200) _messages.RemoveAt(0);
            }
            var handler = OnMessageAdded;
            if (handler != null) handler(msg);
            return msg;
        }

        public void ClearMessages()
        {
            lock (_msgLock) { _messages.Clear(); }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch
                {
                    if (!_running) break;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 60000;
                using (NetworkStream stream = client.GetStream())
                {
                    string headerBlock = ReadHeaderBlock(stream);
                    if (string.IsNullOrEmpty(headerBlock)) return;

                    string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] reqParts = lines[0].Split(' ');
                    if (reqParts.Length < 2) { SendText(stream, 400, "Bad Request", "text/plain", "Bad Request"); return; }

                    string method = reqParts[0].ToUpperInvariant();
                    string rawUrl = reqParts[1];
                    long contentLength = 0;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int idx = lines[i].IndexOf(':');
                        if (idx > 0)
                        {
                            string k = lines[i].Substring(0, idx).Trim();
                            string v = lines[i].Substring(idx + 1).Trim();
                            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                long.TryParse(v, out contentLength);
                        }
                    }

                    int qIdx = rawUrl.IndexOf('?');
                    string path = qIdx >= 0 ? rawUrl.Substring(0, qIdx) : rawUrl;
                    string query = qIdx >= 0 ? rawUrl.Substring(qIdx + 1) : "";

                    if (method == "GET" && path == "/")
                    {
                        SendText(stream, 200, "OK", "text/html; charset=utf-8", WebUiHtml);
                    }
                    else if (method == "GET" && path == "/api/files")
                    {
                        HandleListFiles(stream, GetQueryParam(query, "dir"));
                    }
                    else if (method == "GET" && path == "/api/comic/info")
                    {
                        SendText(stream, 200, "OK", "application/json; charset=utf-8", ComicHelper.GetComicInfoJson(ShareRoot, GetQueryParam(query, "path")));
                    }
                    else if ((method == "GET" || method == "HEAD") && path == "/api/comic/page")
                    {
                        ComicHelper.ServeComicPage(stream, ShareRoot, GetQueryParam(query, "path"), GetQueryParam(query, "entry"), method == "HEAD");
                    }
                    else if ((method == "GET" || method == "HEAD") && path == "/api/download")
                    {
                        HandleDownload(stream, GetQueryParam(query, "path"), GetQueryParam(query, "dl") == "1", method == "HEAD");
                    }
                    else if (method == "POST" && path == "/api/upload")
                    {
                        HandleUpload(stream, GetQueryParam(query, "name"), contentLength);
                    }
                    else if (method == "GET" && path == "/api/messages")
                    {
                        long since = 0;
                        long.TryParse(GetQueryParam(query, "since"), out since);
                        HandleGetMessages(stream, since);
                    }
                    else if (method == "POST" && path == "/api/messages")
                    {
                        HandlePostMessage(stream, contentLength);
                    }
                    else
                    {
                        SendText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not Found");
                    }
                }
            }
            catch { }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private string ReadHeaderBlock(NetworkStream stream)
        {
            MemoryStream ms = new MemoryStream();
            int state = 0;
            while (ms.Length < 32768)
            {
                int b = stream.ReadByte();
                if (b == -1) return null;
                ms.WriteByte((byte)b);
                if (state == 0 && b == '\r') state = 1;
                else if (state == 1 && b == '\n') state = 2;
                else if (state == 2 && b == '\r') state = 3;
                else if (state == 3 && b == '\n') break;
                else state = (b == '\r') ? 1 : 0;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private void HandleListFiles(NetworkStream stream, string relDir)
        {
            string root = Path.GetFullPath(ShareRoot).TrimEnd('\\');
            string cleanRel = (relDir ?? "").Replace('/', '\\').TrimStart('\\');
            string target = string.IsNullOrEmpty(cleanRel) ? root : Path.GetFullPath(Path.Combine(root, cleanRel));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))
            {
                SendText(stream, 403, "Forbidden", "application/json; charset=utf-8", "{\"error\":\"Invalid path\"}");
                return;
            }

            string displayDir = target.Length <= root.Length ? "" : target.Substring(root.Length + 1).Replace('\\', '/');
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"dir\":\"").Append(JsonEscape(displayDir)).Append("\",\"items\":[");
            bool first = true;

            try
            {
                DirectoryInfo di = new DirectoryInfo(target);
                foreach (var d in di.GetDirectories())
                {
                    if ((d.Attributes & FileAttributes.Hidden) != 0 || d.Name.StartsWith(".")) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    string itemRel = string.IsNullOrEmpty(displayDir) ? d.Name : (displayDir + "/" + d.Name);
                    sb.Append("{\"name\":\"").Append(JsonEscape(d.Name))
                      .Append("\",\"path\":\"").Append(JsonEscape(itemRel))
                      .Append("\",\"isDir\":true,\"size\":0}");
                }
                foreach (var f in di.GetFiles())
                {
                    if ((f.Attributes & FileAttributes.Hidden) != 0 || f.Name.StartsWith(".")) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    string itemRel = string.IsNullOrEmpty(displayDir) ? f.Name : (displayDir + "/" + f.Name);
                    sb.Append("{\"name\":\"").Append(JsonEscape(f.Name))
                      .Append("\",\"path\":\"").Append(JsonEscape(itemRel))
                      .Append("\",\"isDir\":false,\"size\":").Append(f.Length).Append("}");
                }
            }
            catch { }

            sb.Append("]}");
            SendText(stream, 200, "OK", "application/json; charset=utf-8", sb.ToString());
        }

        private void HandleDownload(NetworkStream stream, string relPath, bool forceDl, bool headOnly)
        {
            string root = Path.GetFullPath(ShareRoot).TrimEnd('\\');
            string cleanRel = (relPath ?? "").Replace('/', '\\').TrimStart('\\');
            if (string.IsNullOrEmpty(cleanRel)) { SendText(stream, 404, "Not Found", "text/plain", "Not Found"); return; }

            string full = Path.GetFullPath(Path.Combine(root, cleanRel));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                SendText(stream, 404, "Not Found", "text/plain", "Not Found");
                return;
            }

            FileInfo fi = new FileInfo(full);
            string ctype = GetContentType(fi.Extension);
            string disp = forceDl ? ("Content-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(fi.Name) + "\r\n") : "";
            string hdr = string.Format("HTTP/1.1 200 OK\r\nContent-Type: {0}\r\n{1}Content-Length: {2}\r\nConnection: close\r\n\r\n", ctype, disp, fi.Length);
            byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
            stream.Write(hdrBytes, 0, hdrBytes.Length);

            if (!headOnly)
            {
                using (FileStream fs = File.OpenRead(full))
                {
                    byte[] buf = new byte[65536];
                    int r;
                    while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        stream.Write(buf, 0, r);
                    }
                }
            }
            stream.Flush();
        }

        private void HandleUpload(NetworkStream stream, string fileName, long contentLength)
        {
            if (contentLength <= 0 || contentLength > 2L * 1024 * 1024 * 1024)
            {
                SendText(stream, 400, "Bad Request", "application/json; charset=utf-8", "{\"error\":\"Invalid size\"}");
                return;
            }

            string safeName = Path.GetFileName((fileName ?? "upload.bin").Replace('/', '_').Replace('\\', '_'));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";

            Directory.CreateDirectory(ReceiveRoot);
            string destPath = Path.Combine(ReceiveRoot, safeName);
            if (File.Exists(destPath))
            {
                string nameOnly = Path.GetFileNameWithoutExtension(safeName);
                string ext = Path.GetExtension(safeName);
                destPath = Path.Combine(ReceiveRoot, nameOnly + "_" + DateTime.Now.ToString("HHmmss") + ext);
            }

            using (FileStream fs = File.Create(destPath))
            {
                byte[] buf = new byte[65536];
                long remaining = contentLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int r = stream.Read(buf, 0, toRead);
                    if (r <= 0) break;
                    fs.Write(buf, 0, r);
                    remaining -= r;
                }
            }

            var handler = OnFileReceived;
            if (handler != null) handler(destPath);
            SendText(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true,\"saved\":\"" + JsonEscape(Path.GetFileName(destPath)) + "\"}");
        }

        private void HandleGetMessages(NetworkStream stream, long since)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"messages\":[");
            lock (_msgLock)
            {
                bool first = true;
                foreach (var m in _messages)
                {
                    if (m.Id <= since) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"id\":").Append(m.Id)
                      .Append(",\"sender\":\"").Append(JsonEscape(m.Sender))
                      .Append("\",\"text\":\"").Append(JsonEscape(m.Text))
                      .Append("\",\"time\":\"").Append(JsonEscape(m.Time)).Append("\"}");
                }
            }
            sb.Append("]}");
            SendText(stream, 200, "OK", "application/json; charset=utf-8", sb.ToString());
        }

        private void HandlePostMessage(NetworkStream stream, long contentLength)
        {
            if (contentLength <= 0 || contentLength > 1024 * 1024)
            {
                SendText(stream, 400, "Bad Request", "application/json; charset=utf-8", "{\"error\":\"Empty\"}");
                return;
            }
            byte[] buf = new byte[contentLength];
            int total = 0;
            while (total < contentLength)
            {
                int r = stream.Read(buf, total, (int)contentLength - total);
                if (r <= 0) break;
                total += r;
            }
            string text = Encoding.UTF8.GetString(buf, 0, total);
            ChatMessage msg = AddMessage("iPad", text);
            SendText(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true,\"id\":" + (msg != null ? msg.Id : 0) + "}");
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return "";
        }

        private static string GetContentType(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".txt": return "text/plain; charset=utf-8";
                case ".md": return "text/markdown; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".zip": return "application/zip";
                default: return "application/octet-stream";
            }
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

        private static void SendText(NetworkStream stream, int code, string status, string ctype, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string hdr = string.Format("HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n", code, status, ctype, bodyBytes.Length);
            byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
            stream.Write(hdrBytes, 0, hdrBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            Stop();
        }

        private const string WebUiHtml = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<title>KTLanDrop · 电脑 ↔ iPad 互传</title>
<style>
:root{--bg:#f4f6f9;--card:#fff;--primary:#2563eb;--text:#18181b;--muted:#71717a;--border:#e4e4e7;}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;}
body{margin:0;font-family:-apple-system,BlinkMacSystemFont,""PingFang SC"",sans-serif;background:var(--bg);color:var(--text);padding-bottom:24px;}
header{position:sticky;top:0;z-index:10;background:rgba(255,255,255,.92);backdrop-filter:blur(10px);border-bottom:1px solid var(--border);padding:12px 16px;display:flex;align-items:center;justify-content:space-between;gap:12px;}
.brand{font-weight:700;font-size:17px;display:flex;align-items:center;gap:8px;}
.tabs{display:flex;background:#eef2f6;border-radius:10px;padding:3px;gap:4px;}
.tab{border:none;background:transparent;padding:7px 14px;border-radius:8px;font-size:14px;font-weight:600;color:var(--muted);cursor:pointer;position:relative;}
.tab.active{background:#fff;color:var(--primary);box-shadow:0 1px 3px rgba(0,0,0,.08);}
.badge{display:none;position:absolute;top:2px;right:4px;background:#ef4444;color:#fff;font-size:10px;padding:1px 5px;border-radius:99px;}
main{max-width:860px;margin:0 auto;padding:14px;}
.panel{display:none;}
.panel.active{display:block;}
.toolbar{display:flex;gap:8px;margin-bottom:12px;flex-wrap:wrap;}
.search{flex:1;min-width:180px;padding:10px 14px;border:1px solid var(--border);border-radius:10px;font-size:15px;background:#fff;outline:none;}
.search:focus{border-color:var(--primary);}
.upload-btn{background:var(--primary);color:#fff;border:none;border-radius:10px;padding:10px 16px;font-size:14px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;gap:6px;}
.crumb{font-size:13px;color:var(--muted);margin-bottom:10px;display:flex;align-items:center;gap:6px;flex-wrap:wrap;}
.crumb a{color:var(--primary);text-decoration:none;font-weight:500;}
.card{background:var(--card);border:1px solid var(--border);border-radius:12px;overflow:hidden;}
.row{display:flex;align-items:center;justify-content:space-between;padding:12px 14px;border-bottom:1px solid #f4f4f5;gap:10px;}
.row:last-child{border-bottom:none;}
.fname{flex:1;color:var(--text);text-decoration:none;font-size:15px;word-break:break-all;display:flex;align-items:center;gap:8px;}
.meta{display:flex;align-items:center;gap:10px;flex-shrink:0;font-size:12px;color:var(--muted);}
.dl{padding:6px 12px;background:#eff6ff;color:var(--primary);border-radius:8px;text-decoration:none;font-weight:600;font-size:13px;}
.prog{display:none;margin-bottom:12px;background:#fff;border:1px solid var(--border);border-radius:10px;padding:10px 14px;font-size:13px;}
.chat-wrap{display:flex;flex-direction:column;height:calc(100vh - 110px);background:#fff;border:1px solid var(--border);border-radius:14px;overflow:hidden;}
.chat-list{flex:1;overflow-y:auto;padding:14px;display:flex;flex-direction:column;gap:10px;background:#f8fafc;}
.msg{max-width:85%;padding:10px 12px;border-radius:12px;font-size:15px;line-height:1.45;word-break:break-word;position:relative;}
.msg.pc{align-self:flex-start;background:#fff;border:1px solid var(--border);}
.msg.ipad{align-self:flex-end;background:#dbeafe;border:1px solid #bfdbfe;}
.msg-top{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:4px;font-size:11px;color:var(--muted);}
.copy-btn{border:none;background:#f1f5f9;color:#334155;padding:3px 8px;border-radius:6px;font-size:12px;font-weight:600;cursor:pointer;}
.chat-bar{display:flex;gap:8px;padding:10px;border-top:1px solid var(--border);background:#fff;}
.chat-input{flex:1;padding:10px 12px;border:1px solid var(--border);border-radius:10px;font-size:15px;resize:none;height:44px;font-family:inherit;outline:none;}
.send-btn{background:var(--primary);color:#fff;border:none;border-radius:10px;padding:0 18px;font-size:15px;font-weight:600;cursor:pointer;}
.toast{position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:#18181b;color:#fff;padding:8px 16px;border-radius:99px;font-size:13px;opacity:0;transition:opacity .2s;pointer-events:none;z-index:99;}
.toast.show{opacity:1;}
.read-btn{padding:6px 10px;background:#fef3c7;color:#b45309;border:none;border-radius:8px;font-weight:600;font-size:13px;cursor:pointer;}
#readerOverlay{display:none;position:fixed;inset:0;background:#111;color:#fff;z-index:200;flex-direction:column;}
#readerOverlay.open{display:flex;}
.r-bar{position:fixed;left:0;right:0;background:rgba(17,17,17,.9);backdrop-filter:blur(8px);padding:10px 14px;display:flex;align-items:center;justify-content:space-between;gap:8px;z-index:210;transition:opacity .2s,transform .2s;}
.r-top{top:0;border-bottom:1px solid #27272a;}
.r-bot{bottom:0;border-top:1px solid #27272a;}
.r-hidden .r-top{opacity:0;transform:translateY(-100%);pointer-events:none;}
.r-hidden .r-bot{opacity:0;transform:translateY(100%);pointer-events:none;}
.r-btn{background:#27272a;color:#f4f4f5;border:none;border-radius:8px;padding:6px 10px;font-size:13px;font-weight:600;cursor:pointer;white-space:nowrap;}
.r-title{flex:1;font-size:14px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-align:center;}
.r-body{flex:1;overflow-y:auto;-webkit-overflow-scrolling:touch;position:relative;}
.r-webtoon{max-width:900px;margin:0 auto;padding:0;}
.r-webtoon img{width:100%;display:block;min-height:120px;background:#18181b;}
.r-page-box{width:100%;height:100vh;display:flex;align-items:center;justify-content:center;overflow:hidden;user-select:none;}
.r-page-box img{max-width:100%;max-height:100vh;object-fit:contain;}
.r-slider{flex:1;margin:0 8px;}
#chDrawer{display:none;position:fixed;inset:0;z-index:220;background:rgba(0,0,0,.55);}
#chDrawer.open{display:flex;justify-content:flex-end;}
.ch-panel{width:min(320px,82vw);height:100%;background:#18181b;color:#f4f4f5;display:flex;flex-direction:column;border-left:1px solid #27272a;}
.ch-head{padding:14px 16px;font-weight:700;border-bottom:1px solid #27272a;display:flex;justify-content:space-between;align-items:center;}
.ch-list{flex:1;overflow-y:auto;padding:8px;}
.ch-item{padding:11px 12px;border-radius:8px;cursor:pointer;font-size:14px;margin-bottom:4px;word-break:break-all;}
.ch-item.active{background:#2563eb;color:#fff;font-weight:600;}
</style>
</head>
<body>
<header>
  <div class=""brand"">📡 KTLanDrop</div>
  <div class=""tabs"">
    <button class=""tab active"" onclick=""switchTab('files',this)"">📁 文件</button>
    <button class=""tab"" onclick=""switchTab('chat',this)"">💬 文字<span id=""unread"" class=""badge"">0</span></button>
  </div>
</header>
<main>
  <section id=""tab-files"" class=""panel active"">
    <div class=""toolbar"">
      <input id=""search"" class=""search"" type=""search"" placeholder=""🔍 搜索当前目录文件..."" oninput=""renderFiles()"">
      <label class=""upload-btn"">⬆️ 传文件给电脑<input id=""upInput"" type=""file"" multiple style=""display:none"" onchange=""uploadFiles(this.files)""></label>
    </div>
    <div id=""prog"" class=""prog""></div>
    <div id=""crumb"" class=""crumb""></div>
    <div id=""fileList"" class=""card""></div>
  </section>
  <section id=""tab-chat"" class=""panel"">
    <div class=""chat-wrap"">
      <div id=""chatList"" class=""chat-list""></div>
      <div class=""chat-bar"">
        <textarea id=""chatInput"" class=""chat-input"" placeholder=""输入或粘贴文字发给电脑...""></textarea>
        <button class=""send-btn"" onclick=""sendChat()"">发送</button>
      </div>
    </div>
  </section>
</main>
<div id=""toast"" class=""toast""></div>
<div id=""readerOverlay"">
  <div class=""r-bar r-top"">
    <button class=""r-btn"" onclick=""closeReader()"">✕ 关闭</button>
    <div id=""rTitle"" class=""r-title"">加载中...</div>
    <button id=""rModeBtn"" class=""r-btn"" onclick=""toggleReadMode()"">📜 条漫</button>
    <button id=""rDirBtn"" class=""r-btn"" onclick=""toggleRtl()"" style=""display:none"">右→左</button>
    <button class=""r-btn"" onclick=""toggleDrawer(true)"">📑 目录</button>
  </div>
  <div id=""rBody"" class=""r-body""></div>
  <div class=""r-bar r-bot"">
    <button class=""r-btn"" onclick=""stepChapter(-1)"">◀ 上一话</button>
    <input id=""rSlider"" class=""r-slider"" type=""range"" min=""1"" max=""1"" value=""1"" oninput=""jumpPage(this.value-1)"">
    <span id=""rPageTxt"" style=""font-size:12px;min-width:48px;text-align:center"">1/1</span>
    <button class=""r-btn"" onclick=""stepChapter(1)"">下一话 ▶</button>
  </div>
</div>
<div id=""chDrawer"" onclick=""if(event.target===this)toggleDrawer(false)"">
  <div class=""ch-panel"">
    <div class=""ch-head""><span>📑 章节目录</span><button class=""r-btn"" onclick=""toggleDrawer(false)"">✕</button></div>
    <div id=""chList"" class=""ch-list""></div>
  </div>
</div>
<script>
let curDir='', allItems=[], lastMsgId=0, unreadCount=0, curTab='files';
function toast(t){const el=document.getElementById('toast');el.textContent=t;el.classList.add('show');setTimeout(()=>el.classList.remove('show'),1800);}
function switchTab(tab,btn){
  curTab=tab;
  document.querySelectorAll('.tab').forEach(b=>b.classList.remove('active'));
  btn.classList.add('active');
  document.querySelectorAll('.panel').forEach(p=>p.classList.remove('active'));
  document.getElementById('tab-'+tab).classList.add('active');
  if(tab==='chat'){unreadCount=0;document.getElementById('unread').style.display='none';const cl=document.getElementById('chatList');cl.scrollTop=cl.scrollHeight;}
}
function fmtSize(b){if(b>=1048576)return(b/1048576).toFixed(2)+' MB';if(b>=1024)return(b/1024).toFixed(1)+' KB';return b+' B';}
function esc(s){return s.replace(/[&<>""']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]));}
async function loadDir(dir){
  const r=await fetch('/api/files?dir='+encodeURIComponent(dir));
  const d=await r.json();
  curDir=d.dir||''; allItems=d.items||[];
  document.getElementById('search').value='';
  renderCrumb(); renderFiles();
}
function renderCrumb(){
  const c=document.getElementById('crumb');
  let html='<a href=""javascript:loadDir(\'\')"">🏠 根目录</a>';
  if(curDir){
    const parts=curDir.split('/');
    let acc='';
    parts.forEach(p=>{acc=acc?(acc+'/'+p):p;html+=' / <a href=""javascript:loadDir(\''+esc(acc)+'\')"">'+esc(p)+'</a>';});
  }
  c.innerHTML=html;
}
function renderFiles(){
  const q=document.getElementById('search').value.trim().toLowerCase();
  const list=allItems.filter(x=>!q||x.name.toLowerCase().includes(q));
  const box=document.getElementById('fileList');
  if(!list.length){box.innerHTML='<div class=""row"" style=""color:#71717a"">暂无匹配文件</div>';return;}
  box.innerHTML=list.map(x=>{
    if(x.isDir) return `<div class=""row""><a class=""fname"" href=""javascript:loadDir('${esc(x.path)}')"">📁 ${esc(x.name)}/</a><span class=""meta""><button class=""read-btn"" onclick=""openReader('${esc(x.path)}')"">📖 阅读</button></span></div>`;
    const u='/api/download?path='+encodeURIComponent(x.path);
    const isZip=/\.(zip|cbz)$/i.test(x.name);
    const readBtn=isZip?`<button class=""read-btn"" onclick=""openReader('${esc(x.path)}')"">📖 阅读</button>`:'';
    return `<div class=""row""><a class=""fname"" href=""${u}"" target=""_blank"">📄 ${esc(x.name)}</a><span class=""meta""><span>${fmtSize(x.size)}</span>${readBtn}<a class=""dl"" href=""${u}&dl=1"">下载</a></span></div>`;
  }).join('');
}
async function uploadFiles(files){
  if(!files||!files.length)return;
  const p=document.getElementById('prog'); p.style.display='block';
  for(let i=0;i<files.length;i++){
    const f=files[i];
    p.textContent=`正在上传 (${i+1}/${files.length}): ${f.name}...`;
    await new Promise((res,rej)=>{
      const xhr=new XMLHttpRequest();
      xhr.open('POST','/api/upload?name='+encodeURIComponent(f.name));
      xhr.upload.onprogress=e=>{if(e.lengthComputable)p.textContent=`正在上传 (${i+1}/${files.length}): ${f.name} (${Math.round(e.loaded*100/e.total)}%)`;};
      xhr.onload=()=>res(); xhr.onerror=()=>rej(); xhr.send(f);
    });
  }
  p.textContent='✅ 上传完成！已保存到电脑接收目录';
  document.getElementById('upInput').value='';
  setTimeout(()=>p.style.display='none',3000);
}
function copyText(btn,text){
  const ta=document.createElement('textarea');
  ta.value=text; ta.style.position='fixed'; ta.style.opacity='0';
  document.body.appendChild(ta); ta.focus(); ta.select();
  try{document.execCommand('copy');toast('已复制到 iPad 剪贴板');btn.textContent='已复制';setTimeout(()=>btn.textContent='复制',1200);}
  catch{toast('复制失败，请长按文本手动复制');}
  document.body.removeChild(ta);
}
async function pollMsgs(){
  try{
    const r=await fetch('/api/messages?since='+lastMsgId);
    const d=await r.json();
    if(d.messages&&d.messages.length){
      const cl=document.getElementById('chatList');
      d.messages.forEach(m=>{
        lastMsgId=Math.max(lastMsgId,m.id);
        const div=document.createElement('div');
        div.className='msg '+(m.sender==='iPad'?'ipad':'pc');
        const top=document.createElement('div'); top.className='msg-top';
        const info=document.createElement('span'); info.textContent=m.sender+' · '+m.time;
        const btn=document.createElement('button'); btn.className='copy-btn'; btn.textContent='复制';
        btn.onclick=()=>copyText(btn,m.text);
        top.appendChild(info); top.appendChild(btn);
        const body=document.createElement('div'); body.style.whiteSpace='pre-wrap'; body.textContent=m.text;
        div.appendChild(top); div.appendChild(body);
        cl.appendChild(div);
        if(curTab!=='chat'&&m.sender!=='iPad'){unreadCount++;const b=document.getElementById('unread');b.textContent=unreadCount;b.style.display='inline-block';}
      });
      cl.scrollTop=cl.scrollHeight;
    }
  }catch{}
}
async function sendChat(){
  const inp=document.getElementById('chatInput');
  const txt=inp.value.trim(); if(!txt)return;
  inp.value='';
  await fetch('/api/messages',{method:'POST',body:txt});
  pollMsgs();
}
loadDir(''); pollMsgs(); setInterval(pollMsgs,1500);

let rInfo=null, rPage=0, rMode=localStorage.getItem('kt_mode')||'webtoon', rRtl=localStorage.getItem('kt_rtl')!=='0', touchX=0;
function saveProgress(){
  if(!rInfo||!rInfo.series)return;
  localStorage.setItem('kt_prog_'+rInfo.series, JSON.stringify({path:rInfo.current,page:rPage}));
}
async function openReader(path, keepPage){
  const ov=document.getElementById('readerOverlay');
  ov.classList.add('open'); ov.classList.remove('r-hidden');
  const r=await fetch('/api/comic/info?path='+encodeURIComponent(path));
  rInfo=await r.json();
  if(!rInfo.pages||!rInfo.pages.length){toast('该目录或压缩包内没有图片');closeReader();return;}
  rPage=0;
  if(!keepPage&&rInfo.series){
    try{
      const saved=JSON.parse(localStorage.getItem('kt_prog_'+rInfo.series)||'null');
      if(saved&&saved.path===rInfo.current&&saved.page>0&&saved.page<rInfo.pages.length){
        rPage=saved.page; toast('已恢复到第 '+(rPage+1)+' 页');
      }
    }catch{}
  }
  document.getElementById('rTitle').textContent=rInfo.currentName||'阅读';
  renderChapters(); renderReaderBody();
}
function closeReader(){document.getElementById('readerOverlay').classList.remove('open');toggleDrawer(false);}
function toggleDrawer(open){document.getElementById('chDrawer').classList.toggle('open',open);}
function renderChapters(){
  const box=document.getElementById('chList');
  box.innerHTML=(rInfo.chapters||[]).map(c=>`<div class=""ch-item ${c.path===rInfo.current?'active':''}"" onclick=""toggleDrawer(false);openReader('${esc(c.path)}',true)"">${c.type==='zip'?'📦':'📁'} ${esc(c.name)}</div>`).join('');
}
function toggleReadMode(){
  rMode=(rMode==='webtoon')?'page':'webtoon';
  localStorage.setItem('kt_mode',rMode);
  renderReaderBody();
}
function toggleRtl(){
  rRtl=!rRtl; localStorage.setItem('kt_rtl',rRtl?'1':'0');
  document.getElementById('rDirBtn').textContent=rRtl?'右→左':'左→右';
}
function renderReaderBody(){
  document.getElementById('rModeBtn').textContent=rMode==='webtoon'?'📜 条漫':'📖 页漫';
  document.getElementById('rDirBtn').style.display=rMode==='page'?'inline-block':'none';
  document.getElementById('rDirBtn').textContent=rRtl?'右→左':'左→右';
  const sl=document.getElementById('rSlider');
  sl.max=rInfo.pages.length; sl.value=rPage+1;
  document.getElementById('rPageTxt').textContent=(rPage+1)+'/'+rInfo.pages.length;
  const body=document.getElementById('rBody');
  if(rMode==='webtoon'){
    body.innerHTML='<div class=""r-webtoon"" onclick=""toggleBars()"">'+rInfo.pages.map((u,i)=>`<img loading=""lazy"" data-idx=""${i}"" src=""${u}"">`).join('')+'</div>';
    body.onscroll=()=>{
      const imgs=body.querySelectorAll('img');
      for(let i=0;i<imgs.length;i++){
        const rc=imgs[i].getBoundingClientRect();
        if(rc.bottom>100){rPage=i;sl.value=i+1;document.getElementById('rPageTxt').textContent=(i+1)+'/'+rInfo.pages.length;saveProgress();break;}
      }
    };
    if(rPage>0){const t=body.querySelectorAll('img')[rPage];if(t)setTimeout(()=>t.scrollIntoView(),120);}
  }else{
    body.onscroll=null;
    body.innerHTML=`<div class=""r-page-box"" id=""rPageBox""><img id=""rCurImg"" src=""${rInfo.pages[rPage]}""></div>`;
    const pb=document.getElementById('rPageBox');
    pb.onclick=e=>{
      const x=e.clientX/window.innerWidth;
      if(x<0.3) turnPage(rRtl?1:-1);
      else if(x>0.7) turnPage(rRtl?-1:1);
      else toggleBars();
    };
    pb.ontouchstart=e=>{touchX=e.changedTouches[0].clientX;};
    pb.ontouchend=e=>{
      const dx=e.changedTouches[0].clientX-touchX;
      if(Math.abs(dx)>45) turnPage((dx>0)==rRtl?1:-1);
    };
    preloadNext();
  }
  saveProgress();
}
function toggleBars(){document.getElementById('readerOverlay').classList.toggle('r-hidden');}
function preloadNext(){
  for(let k=1;k<=2;k++){if(rPage+k<rInfo.pages.length){const img=new Image();img.src=rInfo.pages[rPage+k];}}
}
function jumpPage(idx){
  rPage=Math.max(0,Math.min(rInfo.pages.length-1,idx));
  document.getElementById('rSlider').value=rPage+1;
  document.getElementById('rPageTxt').textContent=(rPage+1)+'/'+rInfo.pages.length;
  if(rMode==='webtoon'){const t=document.querySelectorAll('#rBody img')[rPage];if(t)t.scrollIntoView();}
  else{const im=document.getElementById('rCurImg');if(im)im.src=rInfo.pages[rPage];preloadNext();}
  saveProgress();
}
function turnPage(delta){
  const next=rPage+delta;
  if(next<0){stepChapter(-1);return;}
  if(next>=rInfo.pages.length){stepChapter(1);return;}
  jumpPage(next);
}
function stepChapter(delta){
  if(!rInfo||!rInfo.chapters)return;
  const idx=rInfo.chapters.findIndex(c=>c.path===rInfo.current);
  const ni=idx+delta;
  if(ni<0){toast('已经是第一话了');return;}
  if(ni>=rInfo.chapters.length){toast('已经是最后一话了');return;}
  openReader(rInfo.chapters[ni].path,true);
}
</script>
</body>
</html>";
    }
}
