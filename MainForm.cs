using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace KTLanDrop
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private readonly string _settingsPath;
        private HttpServer _server;

        private string _shareDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        private string _receiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "iPad接收");
        private int _port = 8888;

        private Label _lblStatus;
        private Button _btnToggle;
        private Label _lblFwStatus;
        private Button _btnFw;
        private TextBox _txtUrl;
        private PictureBox _picQr;
        private TextBox _txtShareDir;
        private TextBox _txtReceiveDir;
        private NumericUpDown _numPort;
        private FlowLayoutPanel _chatFlow;
        private TextBox _txtChatInput;
        private Label _lblReceiveLog;

        public MainForm()
        {
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KTLanDrop");
            Directory.CreateDirectory(appDir);
            _settingsPath = Path.Combine(appDir, "settings.ini");
            LoadSettings();

            InitializeUi();
            StartServer(autoFallbackPort: true);
            RefreshNetworkAndFirewall();
        }

        private void InitializeUi()
        {
            Text = "KTLanDrop · 电脑 ↔ iPad 局域网互传";
            Size = new Size(960, 650);
            MinimumSize = new Size(880, 580);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(244, 246, 249);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            // Top header with left/right FlowLayoutPanels so controls never overlap at any DPI
            Panel header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10) };
            header.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(228, 228, 231)), 0, header.Height - 1, header.Width, header.Height - 1);

            FlowLayoutPanel headerLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 0)
            };
            Label title = new Label
            {
                Text = "📡 KTLanDrop 局域网互传",
                Font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(24, 24, 27),
                AutoSize = true,
                Margin = new Padding(0, 2, 12, 0)
            };
            _lblStatus = new Label
            {
                Text = "● 服务未启动",
                ForeColor = Color.Gray,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                Margin = new Padding(0, 6, 0, 0)
            };
            headerLeft.Controls.Add(title);
            headerLeft.Controls.Add(_lblStatus);

            FlowLayoutPanel headerRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 2, 0, 0)
            };
            _lblFwStatus = new Label
            {
                Text = "防火墙检测中...",
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 116, 139),
                Margin = new Padding(0, 8, 10, 0)
            };
            _btnFw = CreateFlatButton("一键放行防火墙", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            _btnFw.Size = new Size(128, 34);
            _btnFw.Margin = new Padding(0, 0, 10, 0);
            _btnFw.Click += BtnFw_Click;

            _btnToggle = CreateFlatButton("停止服务", Color.FromArgb(239, 68, 68), Color.White);
            _btnToggle.Size = new Size(96, 34);
            _btnToggle.Margin = new Padding(0);
            _btnToggle.Click += (s, e) => { if (_server != null && _server.IsRunning) StopServer(); else StartServer(false); };

            headerRight.Controls.Add(_lblFwStatus);
            headerRight.Controls.Add(_btnFw);
            headerRight.Controls.Add(_btnToggle);

            header.Controls.Add(headerRight);
            header.Controls.Add(headerLeft);

            // Main container
            TableLayoutPanel main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(14) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 370));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Left column: QR + Folders + Port
            Panel leftCol = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };

            Panel qrCard = CreateCard(0, 0, 360, 286);
            qrCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Label lblQrTip = new Label { Text = "iPad 扫码或 Safari 访问以下网址", Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold), ForeColor = Color.FromArgb(39, 39, 42), Location = new Point(14, 12), AutoSize = true };
            Button btnRefreshIp = CreateFlatButton("刷新IP", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnRefreshIp.Size = new Size(64, 24);
            btnRefreshIp.Font = new Font("Microsoft YaHei UI", 8.5f);
            btnRefreshIp.Location = new Point(282, 10);
            btnRefreshIp.Click += (s, e) => RefreshNetworkAndFirewall();

            _picQr = new PictureBox { Location = new Point(90, 42), Size = new Size(180, 180), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
            _txtUrl = new TextBox { ReadOnly = true, Location = new Point(14, 236), Size = new Size(252, 28), BackColor = Color.FromArgb(248, 250, 252), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) };
            Button btnCopyUrl = CreateFlatButton("复制", Color.FromArgb(37, 99, 235), Color.White);
            btnCopyUrl.Location = new Point(274, 234);
            btnCopyUrl.Size = new Size(72, 30);
            btnCopyUrl.Click += (s, e) => { Clipboard.SetText(_txtUrl.Text); btnCopyUrl.Text = "已复制"; };
            qrCard.Controls.AddRange(new Control[] { lblQrTip, btnRefreshIp, _picQr, _txtUrl, btnCopyUrl });

            Panel dirCard = CreateCard(0, 298, 360, 252);
            dirCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Label lblShare = new Label { Text = "📁 电脑共享给 iPad 的目录：", Location = new Point(14, 12), AutoSize = true, ForeColor = Color.FromArgb(63, 63, 70) };
            _txtShareDir = new TextBox { ReadOnly = true, Text = _shareDir, Location = new Point(14, 36), Size = new Size(224, 26), BackColor = Color.FromArgb(248, 250, 252) };
            Button btnSharePick = CreateFlatButton("更改", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnSharePick.Location = new Point(244, 34); btnSharePick.Size = new Size(50, 28);
            btnSharePick.Click += (s, e) => PickFolder(ref _shareDir, _txtShareDir, true);
            Button btnShareOpen = CreateFlatButton("打开", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnShareOpen.Location = new Point(298, 34); btnShareOpen.Size = new Size(48, 28);
            btnShareOpen.Click += (s, e) => OpenFolder(_shareDir);

            Label lblRecv = new Label { Text = "📥 iPad 传回电脑的接收目录：", Location = new Point(14, 74), AutoSize = true, ForeColor = Color.FromArgb(63, 63, 70) };
            _txtReceiveDir = new TextBox { ReadOnly = true, Text = _receiveDir, Location = new Point(14, 98), Size = new Size(224, 26), BackColor = Color.FromArgb(248, 250, 252) };
            Button btnRecvPick = CreateFlatButton("更改", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnRecvPick.Location = new Point(244, 96); btnRecvPick.Size = new Size(50, 28);
            btnRecvPick.Click += (s, e) => PickFolder(ref _receiveDir, _txtReceiveDir, false);
            Button btnRecvOpen = CreateFlatButton("打开", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnRecvOpen.Location = new Point(298, 96); btnRecvOpen.Size = new Size(48, 28);
            btnRecvOpen.Click += (s, e) => { Directory.CreateDirectory(_receiveDir); OpenFolder(_receiveDir); };

            Label lblPort = new Label { Text = "🔌 服务端口：", Location = new Point(14, 140), AutoSize = true, ForeColor = Color.FromArgb(63, 63, 70) };
            _numPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = _port, Location = new Point(108, 137), Size = new Size(80, 26) };
            Button btnApplyPort = CreateFlatButton("应用端口", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnApplyPort.Location = new Point(196, 136); btnApplyPort.Size = new Size(80, 28);
            btnApplyPort.Click += (s, e) => ApplyNewPort((int)_numPort.Value);

            _lblReceiveLog = new Label { Text = "提示：iPad 传来的文本不会覆盖电脑剪贴板，可按需点复制。", Location = new Point(14, 178), Size = new Size(332, 54), ForeColor = Color.FromArgb(113, 113, 122), Font = new Font("Microsoft YaHei UI", 8.5f) };
            dirCard.Controls.AddRange(new Control[] { lblShare, _txtShareDir, btnSharePick, btnShareOpen, lblRecv, _txtReceiveDir, btnRecvPick, btnRecvOpen, lblPort, _numPort, btnApplyPort, _lblReceiveLog });

            leftCol.Controls.Add(qrCard);
            leftCol.Controls.Add(dirCard);

            // Right column: Chat box
            Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(1) };
            rightCard.Paint += (s, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(228, 228, 231)), 0, 0, rightCard.Width - 1, rightCard.Height - 1);

            Panel chatHeader = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(14, 10, 14, 8) };
            Label lblChatTitle = new Label { Text = "💬 文字互传（选择性复制）", Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(14, 12) };
            Button btnClearChat = CreateFlatButton("清空记录", Color.White, Color.FromArgb(100, 116, 139));
            btnClearChat.Size = new Size(76, 26);
            btnClearChat.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClearChat.Location = new Point(chatHeader.Width - 90, 9);
            btnClearChat.Click += (s, e) => { if (_server != null) _server.ClearMessages(); _chatFlow.Controls.Clear(); };
            chatHeader.Controls.AddRange(new Control[] { lblChatTitle, btnClearChat });

            _chatFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(12) };
            _chatFlow.SizeChanged += (s, e) => { foreach (Control c in _chatFlow.Controls) c.Width = Math.Max(200, _chatFlow.ClientSize.Width - 28); };

            Panel chatBottom = new Panel { Dock = DockStyle.Bottom, Height = 82, BackColor = Color.White, Padding = new Padding(12) };
            chatBottom.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(228, 228, 231)), 0, 0, chatBottom.Width, 0);
            _txtChatInput = new TextBox { Multiline = true, Location = new Point(12, 12), Size = new Size(chatBottom.Width - 122, 58), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 10f) };
            _txtChatInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendPcMessage(); } };
            Button btnSend = CreateFlatButton("发送给 iPad", Color.FromArgb(37, 99, 235), Color.White);
            btnSend.Size = new Size(96, 58);
            btnSend.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            btnSend.Location = new Point(chatBottom.Width - 106, 12);
            btnSend.Click += (s, e) => SendPcMessage();
            chatBottom.Controls.AddRange(new Control[] { _txtChatInput, btnSend });

            rightCard.Controls.Add(_chatFlow);
            rightCard.Controls.Add(chatBottom);
            rightCard.Controls.Add(chatHeader);

            main.Controls.Add(leftCol, 0, 0);
            main.Controls.Add(rightCard, 1, 0);

            Controls.Add(main);
            Controls.Add(header);
        }

        private void SendPcMessage()
        {
            string txt = _txtChatInput.Text.Trim();
            if (string.IsNullOrEmpty(txt) || _server == null) return;
            _txtChatInput.Clear();
            _server.AddMessage("电脑", txt);
        }

        private void AddMessageCard(ChatMessage msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<ChatMessage>(AddMessageCard), msg); return; }

            bool isPc = msg.Sender == "电脑";
            Panel card = new Panel
            {
                Width = Math.Max(200, _chatFlow.ClientSize.Width - 28),
                BackColor = isPc ? Color.FromArgb(239, 246, 255) : Color.White,
                Padding = new Padding(10),
                Margin = new Padding(0, 0, 0, 8)
            };

            Label lblMeta = new Label { Text = msg.Sender + " · " + msg.Time, ForeColor = Color.FromArgb(113, 113, 122), Font = new Font("Microsoft YaHei UI", 8.5f), AutoSize = true, Location = new Point(10, 8) };
            Button btnCopy = CreateFlatButton("复制", Color.FromArgb(241, 245, 249), Color.FromArgb(30, 41, 59));
            btnCopy.Size = new Size(54, 24);
            btnCopy.Font = new Font("Microsoft YaHei UI", 8.5f);
            btnCopy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCopy.Location = new Point(card.Width - 64, 5);
            btnCopy.Click += (s, e) => { Clipboard.SetText(msg.Text); btnCopy.Text = "已复制"; };

            TextBox txtBody = new TextBox
            {
                Text = msg.Text,
                ReadOnly = true,
                Multiline = true,
                BorderStyle = BorderStyle.None,
                BackColor = card.BackColor,
                ForeColor = Color.FromArgb(24, 24, 27),
                Location = new Point(10, 32),
                Width = card.Width - 24,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Size measured = TextRenderer.MeasureText(msg.Text, txtBody.Font, new Size(txtBody.Width, int.MaxValue), TextFormatFlags.WordBreak);
            txtBody.Height = Math.Max(22, measured.Height + 6);
            card.Height = txtBody.Bottom + 10;

            card.Controls.AddRange(new Control[] { lblMeta, btnCopy, txtBody });
            _chatFlow.Controls.Add(card);
            _chatFlow.ScrollControlIntoView(card);
        }

        private void StartServer(bool autoFallbackPort)
        {
            if (!Directory.Exists(_shareDir)) _shareDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            int attempts = autoFallbackPort ? 10 : 1;
            Exception lastEx = null;

            for (int i = 0; i < attempts; i++)
            {
                int tryPort = _port + i;
                try
                {
                    var srv = new HttpServer(_shareDir, _receiveDir, tryPort);
                    srv.OnMessageAdded += AddMessageCard;
                    srv.OnFileReceived += path =>
                    {
                        if (InvokeRequired) BeginInvoke(new Action(() => _lblReceiveLog.Text = "✅ 收到新文件：" + Path.GetFileName(path)));
                    };
                    srv.Start();
                    _server = srv;
                    _port = tryPort;
                    if (_numPort != null) _numPort.Value = _port;
                    _lblStatus.Text = "● 运行中 (端口 " + _port + ")";
                    _lblStatus.ForeColor = Color.FromArgb(22, 163, 74);
                    _btnToggle.Text = "停止服务";
                    _btnToggle.BackColor = Color.FromArgb(239, 68, 68);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }
            MessageBox.Show("启动服务失败：" + (lastEx != null ? lastEx.Message : "端口被占用"), "KTLanDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void ApplyNewPort(int newPort)
        {
            if (newPort == _port && _server != null && _server.IsRunning) return;
            StopServer();
            _port = newPort;
            StartServer(false);
            SaveSettings();
            RefreshNetworkAndFirewall();
        }

        private void StopServer()
        {
            if (_server != null) { _server.Stop(); _server = null; }
            _lblStatus.Text = "● 已停止";
            _lblStatus.ForeColor = Color.FromArgb(113, 113, 122);
            _btnToggle.Text = "启动服务";
            _btnToggle.BackColor = Color.FromArgb(37, 99, 235);
        }

        private void RefreshNetworkAndFirewall()
        {
            string ip = FirewallHelper.GetBestLanIp();
            string url = "http://" + ip + ":" + _port;
            _txtUrl.Text = url;
            if (_picQr.Image != null) _picQr.Image.Dispose();
            _picQr.Image = QrGenerator.Generate(url, 6);

            bool ok = FirewallHelper.IsFirewallRulePresent(_port);
            _lblFwStatus.Text = ok ? "🛡️ 防火墙已放行 (局域网)" : "⚠️ 未配置防火墙规则";
            _lblFwStatus.ForeColor = ok ? Color.FromArgb(22, 163, 74) : Color.FromArgb(217, 119, 6);
        }

        private void BtnFw_Click(object sender, EventArgs e)
        {
            string err;
            if (FirewallHelper.AddLanFirewallRuleWithUac(_port, out err))
            {
                RefreshNetworkAndFirewall();
                MessageBox.Show("已添加局域网防火墙放行规则（端口 " + _port + "），以后启动不再弹窗！", "KTLanDrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("未能添加规则：" + (err ?? "用户取消或权限不足"), "KTLanDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PickFolder(ref string field, TextBox box, bool isShare)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { SelectedPath = Directory.Exists(field) ? field : "" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    field = fbd.SelectedPath;
                    box.Text = field;
                    if (_server != null)
                    {
                        if (isShare) _server.ShareRoot = field;
                        else _server.ReceiveRoot = field;
                    }
                    SaveSettings();
                }
            }
        }

        private static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", path);
        }

        private void LoadSettings()
        {
            if (!File.Exists(_settingsPath)) return;
            foreach (string line in File.ReadAllLines(_settingsPath, Encoding.UTF8))
            {
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string k = line.Substring(0, idx).Trim();
                string v = line.Substring(idx + 1).Trim();
                if (k == "ShareDir" && Directory.Exists(v)) _shareDir = v;
                else if (k == "ReceiveDir" && !string.IsNullOrEmpty(v)) _receiveDir = v;
                else if (k == "Port")
                {
                    int p;
                    if (int.TryParse(v, out p) && p >= 1024 && p <= 65535) _port = p;
                }
            }
        }

        private void SaveSettings()
        {
            File.WriteAllLines(_settingsPath, new[]
            {
                "ShareDir=" + _shareDir,
                "ReceiveDir=" + _receiveDir,
                "Port=" + _port
            }, Encoding.UTF8);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            StopServer();
            base.OnFormClosing(e);
        }

        private static Panel CreateCard(int x, int y, int w, int h)
        {
            Panel p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Color.White };
            p.Paint += (s, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(228, 228, 231)), 0, 0, p.Width - 1, p.Height - 1);
            return p;
        }

        private static Button CreateFlatButton(string text, Color bg, Color fg)
        {
            Button b = new Button { Text = text, BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
