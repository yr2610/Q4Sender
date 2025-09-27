using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using QRCoder;
using static QRCoder.QRCodeGenerator;

namespace Q4Sender
{
    public partial class Form1 : Form
    {
        // ---- 状態 ----
        private string[] _lines = Array.Empty<string>();
        private int _idx = 0;
        private bool _paused = false;
        private bool _fullscreen = false;

        // タイマ
        private System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private System.Windows.Forms.Timer _helpAutoHide = new System.Windows.Forms.Timer { Interval = 4000 };

        // UI
        private PictureBox _pictureBox;
        private Panel _helpOverlay;
        private Label _helpLabel;

        public Form1()
        {
            InitializeComponent(); // Designer 有無どちらでもOK（後で上書き）
            Text = "Q4Sender";
            StartPosition = FormStartPosition.CenterScreen;

            // ==== 初期サイズ：小さめ（以前の 900x700 より さらに小さく）====
            Width = 480; Height = 360;  // ご要望通り、半分以下のサイズ感

            KeyPreview = true;

            // いったん Designer のコントロールをクリア（クリーンに構成）
            Controls.Clear();

            // 画像表示領域
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            Controls.Add(_pictureBox);
            _pictureBox.BringToFront();

            // ヘルプオーバーレイ（半透明）
            _helpOverlay = new Panel
            {
                BackColor = Color.FromArgb(180, 15, 23, 42), // 半透明ダーク（#0F172A）
                Padding = new Padding(10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            _helpLabel = new Label
            {
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Regular),
                Text =
@"操作:
  Ctrl+O : ファイルを開く（任意ファイル or Q4行テキスト）
  Space  : 一時停止/再開
  F      : 全画面切替
  ← / →  : 前 / 次
  Esc    : 終了
  F1     : ヘルプ表示"
            };
            _helpOverlay.Controls.Add(_helpLabel);
            Controls.Add(_helpOverlay);

            // 配置（左上に少し余白を空ける）
            _helpOverlay.Left = 10;
            _helpOverlay.Top = 10;
            _helpOverlay.BringToFront();
            _helpOverlay.Visible = true; // 起動時は表示

            // 自動非表示タイマ（4秒）
            _helpAutoHide.Tick += (s, e) => { _helpAutoHide.Stop(); _helpOverlay.Visible = false; };
            _helpAutoHide.Start();

            // ちらつき低減
            this.DoubleBuffered = true;
            this.BackColor = Color.White;

            // イベント
            _timer.Tick += (s, e) => ShowNext();
            KeyDown += Form1_KeyDown;

            Shown += (s, e) =>
            {
                _pictureBox.BackColor = Color.White;
            };
        }

        // ========= キー操作 =========
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Space)
            {
                _paused = !_paused;
                if (_paused) _timer.Stop(); else _timer.Start();
            }
            else if (e.Control && e.KeyCode == Keys.O) LoadAny();         // 任意ファイル or Q4テキスト
            else if (e.KeyCode == Keys.F) ToggleFullScreen();             // 全画面
            else if (e.KeyCode == Keys.Right) ShowNext();                 // 次へ
            else if (e.KeyCode == Keys.Left) ShowPrev();                  // 前へ
            else if (e.KeyCode == Keys.F1) ShowHelpOverlay();             // ヘルプ再表示
        }

        private void ShowHelpOverlay()
        {
            _helpOverlay.Visible = true;
            _helpOverlay.BringToFront();
            _helpAutoHide.Stop();
            _helpAutoHide.Start(); // 4秒後に自動で消える
        }

        private void ToggleFullScreen()
        {
            _fullscreen = !_fullscreen;
            if (_fullscreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                TopMost = true;
                BackColor = Color.White;
                _pictureBox.BackColor = Color.White;
            }
            else
            {
                TopMost = false;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.Sizable;
                BackColor = SystemColors.Control;
            }
        }

        // ========= 読み込み（任意ファイル or 既成Q4テキスト） =========
        private void LoadAny()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "すべてのファイル|*.*",
                Title = "送信するファイルを選択（任意）／またはQ4テキストを選択"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            var path = ofd.FileName;

            // まず「Q4テキスト」として読めるか軽く判定
            string[] asTextQ4 = Array.Empty<string>();
            try
            {
                var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
                asTextQ4 = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.StartsWith("Q4|", StringComparison.OrdinalIgnoreCase))
                               .ToArray();
            }
            catch { /* バイナリ等で失敗してもOK */ }

            if (asTextQ4.Length > 0)
            {
                _lines = asTextQ4;
                Text = $"Q4Sender - 既成Q4行 {_lines.Length} 枚";
            }
            else
            {
                try
                {
                    var (packed, sid) = PackFileToQ4Lines(path, payloadLen: 700);
                    _lines = packed;
                    Text = $"Q4Sender - SID={sid} 生成 {_lines.Length} 枚";

                    // 生成結果を .q4.txt として書き出し（任意）
                    try
                    {
                        var outTxt = Path.ChangeExtension(path, ".q4.txt");
                        File.WriteAllLines(outTxt, _lines, new UTF8Encoding(false));
                    }
                    catch { /* 無視 */ }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "パッキングに失敗しました: " + ex.Message, "Q4Sender",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            _idx = 0;
            _paused = false;
            _timer.Start();
            ShowCurrent();
        }

        // ========= 表示制御 =========
        private void ShowNext()
        {
            if (_lines.Length == 0) return;
            _idx = (_idx + 1) % _lines.Length;
            ShowCurrent();
        }
        private void ShowPrev()
        {
            if (_lines.Length == 0) return;
            _idx = (_idx - 1 + _lines.Length) % _lines.Length;
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            if (_lines.Length == 0) return;

            try
            {
                var line = _lines[_idx];

                // QRCoder で生成（誤り訂正 Q 推奨 / M でも可）
                using var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(line, QRCodeGenerator.ECCLevel.Q,
                                            forceUtf8: true, utf8BOM: false, EciMode.Utf8);

                using var qr = new QRCode(data);
                using var bmp = qr.GetGraphic(
                    pixelsPerModule: 16,       // 小さめウィンドウでも見やすいよう大きめ
                    Color.Black,
                    Color.White,
                    drawQuietZones: true);

                _pictureBox.BackColor = Color.White;
                _pictureBox.Image?.Dispose();
                _pictureBox.Image = (Bitmap)bmp.Clone();

                // タイトルは控えめに（重くしない）
                // Text = $"Q4Sender - {(_idx + 1)}/{_lines.Length} - {DateTime.Now:T}";
            }
            catch (Exception ex)
            {
                _timer.Stop();
                MessageBox.Show(this, "QR生成・描画でエラー: " + ex.Message, "Q4Sender",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ========= パッカー（任意ファイル → Q4行） =========

        // Base64URL（= と末尾パディング除去）
        private static string Base64UrlNoPad(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return s;
        }

        // 3文字の Base36 SID
        private static string MakeSid(int len = 3)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            using var rng = RandomNumberGenerator.Create();
            var buf = new byte[len]; rng.GetBytes(buf);
            var sb = new StringBuilder(len);
            foreach (var b in buf) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        // 任意ファイル → gzip → Base64URL → 固定長分割 → Q4行
        private static (string[] lines, string sid) PackFileToQ4Lines(string filePath, int payloadLen = 700, string? sid = null)
        {
            sid ??= MakeSid(3);

            // 読み込み（バイナリOK）
            byte[] raw = File.ReadAllBytes(filePath);

            // gzip圧縮
            byte[] gz;
            using (var msOut = new MemoryStream())
            {
                using (var gzStream = new GZipStream(msOut, CompressionLevel.Optimal, leaveOpen: true))
                    gzStream.Write(raw, 0, raw.Length);
                gz = msOut.ToArray();
            }

            // Base64URL（パディング無）
            var b64u = Base64UrlNoPad(gz);

            // 分割
            var parts = Enumerable.Range(0, (int)Math.Ceiling(b64u.Length / (double)payloadLen))
                                  .Select(i => b64u.Substring(i * payloadLen, Math.Min(payloadLen, b64u.Length - i * payloadLen)))
                                  .ToArray();
            int tot = parts.Length;
            if (tot < 1 || tot > 0xFFFF) throw new InvalidOperationException("分割数が範囲外です");

            // Q4行（idx/tot は16進）
            var lines = parts.Select((p, i) => $"Q4|{(i + 1).ToString("X")}/{tot.ToString("X")}|{sid}|{p}")
                             .ToArray();

            return (lines, sid);
        }
    }
}
