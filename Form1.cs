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
        private const int DefaultPayloadLength = 700;
        private const int MaxQrByteCapacity = 2953; // Version40-L（Byteモード）の上限
        private const int QrLineOverheadWorstCase = 17; // "Q4|FFFF/FFFF|SID|" の最大オーバーヘッド

        private readonly AppConfig _config;
        private readonly QRCodeGenerator.ECCLevel _effectiveEccLevel;
        private readonly string? _invalidEccValue;
        private bool _eccWarningShown;
        private int? _configuredVersion;
        private bool _versionFallbackMessageShown;
        private string[] _lines = Array.Empty<string>();
        private int _idx = 0;
        private bool _paused = false;
        private bool _fullscreen = false;

        // タイマ
        private System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 250 };
        private System.Windows.Forms.Timer _helpAutoHide = new System.Windows.Forms.Timer { Interval = 4000 };

        // UI
        private PictureBox _pictureBox;
        private Panel _helpOverlay;
        private Label _helpLabel;
        private Label _counterLabel;

        public Form1()
        {
            try
            {
                _config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "conf.yaml"));
            }
            catch
            {
                _config = AppConfig.CreateDefault();
            }

            var eccSetting = _config.QrSettings.ErrorCorrectionLevel;
            if (!string.IsNullOrWhiteSpace(eccSetting) &&
                Enum.TryParse(eccSetting.Trim(), true, out QRCodeGenerator.ECCLevel parsedEcc))
            {
                _effectiveEccLevel = parsedEcc;
                _invalidEccValue = null;
            }
            else
            {
                _effectiveEccLevel = QRCodeGenerator.ECCLevel.Q;
                var trimmed = eccSetting?.Trim();
                _invalidEccValue = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            }

            _configuredVersion = _config.QrSettings.Version;

            InitializeComponent(); // Designer 有無どちらでもOK（後で上書き）
            Text = "Q4Sender";
            StartPosition = FormStartPosition.CenterScreen;

            // ==== 初期サイズ：小さめ ====
            Width = 480; Height = 360;

            KeyPreview = true;

            // いったん Designer のコントロールをクリア（クリーンに構成）
            Controls.Clear();

            // レイアウト全体（QRとカウンタ行）
            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                Dock = DockStyle.Fill,
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            // 画像表示領域
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            layout.Controls.Add(_pictureBox, 0, 0);

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

            // QR番号表示用ラベル
            var counterPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(180, 15, 23, 42),
                Padding = new Padding(0, 6, 20, 6),
            };
            layout.Controls.Add(counterPanel, 0, 1);

            _counterLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold),
                Text = "0 / 0",
                TextAlign = ContentAlignment.MiddleRight
            };
            _counterLabel.Dock = DockStyle.Right;
            _counterLabel.Padding = new Padding(10, 0, 10, 0);
            counterPanel.Controls.Add(_counterLabel);

            // 配置（左上・右下に余白を空ける）
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
                UpdateCounterLabel();
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
                    var payloadLen = DeterminePayloadLength(out var configWarning);
                    var (packed, sid) = PackFileToQ4Lines(path, payloadLen: payloadLen);
                    _lines = packed;
                    Text = $"Q4Sender - SID={sid} 生成 {_lines.Length} 枚";

                    if (!string.IsNullOrEmpty(configWarning))
                    {
                        MessageBox.Show(this, configWarning, "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

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
            if (_lines.Length == 0)
            {
                UpdateCounterLabel();
                return;
            }

            if (_invalidEccValue != null && !_eccWarningShown)
            {
                MessageBox.Show(this,
                    $"QR誤り訂正レベルの設定値 '{_invalidEccValue}' は無効です。既定値(Q)を使用します。",
                    "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _eccWarningShown = true;
            }

            try
            {
                var line = _lines[_idx];

                // QRCoder で生成（誤り訂正 Q 推奨 / M でも可）
                using var gen = new QRCodeGenerator();
                var requestedVersion = _configuredVersion ?? -1;

                QRCodeData? data = null;
                try
                {
                    data = gen.CreateQrCode(line, _effectiveEccLevel,
                        forceUtf8: true, utf8BOM: false, EciMode.Utf8,
                        requestedVersion: requestedVersion);
                }
                catch (DataTooLongException) when (_configuredVersion != null)
                {
                    var failedVersion = _configuredVersion.Value;
                    if (!_versionFallbackMessageShown)
                    {
                        MessageBox.Show(this,
                            $"QRバージョン {failedVersion} の設定ではコンテンツを収容できません。自動サイズに戻して再生成します。",
                            "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _versionFallbackMessageShown = true;
                    }

                    _configuredVersion = null;
                    data = gen.CreateQrCode(line, _effectiveEccLevel,
                        forceUtf8: true, utf8BOM: false, EciMode.Utf8,
                        requestedVersion: -1);
                }

                if (data == null)
                {
                    throw new InvalidOperationException("QR コードデータの生成に失敗しました。");
                }

                using (data)
                {
                    using var qr = new QRCode(data);
                    using var bmp = qr.GetGraphic(
                        pixelsPerModule: 16,       // 小さめウィンドウでも見やすいよう大きめ
                        Color.Black,
                        Color.White,
                        drawQuietZones: true);

                    _pictureBox.BackColor = Color.White;
                    _pictureBox.Image?.Dispose();
                    _pictureBox.Image = (Bitmap)bmp.Clone();

                    UpdateCounterLabel();
                }

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

        // 任意ファイル → zip（単一エントリ）→ Base64URL → 固定長分割 → Q4行
        private static (string[] lines, string sid) PackFileToQ4Lines(string filePath, int payloadLen = DefaultPayloadLength, string? sid = null)
        {
            if (payloadLen <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLen));
            }

            sid ??= MakeSid(3);

            // 読み込み（バイナリOK）
            byte[] raw = File.ReadAllBytes(filePath);

            // zip圧縮（ディレクトリ無し、1ファイル）
            byte[] zipped;
            using (var msOut = new MemoryStream())
            {
                using (var zip = new ZipArchive(msOut, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entryName = Path.GetFileName(filePath);
                    if (string.IsNullOrEmpty(entryName)) entryName = "payload";
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    entryStream.Write(raw, 0, raw.Length);
                }
                zipped = msOut.ToArray();
            }

            // Base64URL（パディング無）
            var b64u = Base64UrlNoPad(zipped);

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

        private int DeterminePayloadLength(out string? warningMessage)
        {
            warningMessage = null;

            var requestedVersion = _configuredVersion ?? -1;
            var capacity = TryGetMaxQrCapacity(_effectiveEccLevel, requestedVersion);

            if (!capacity.HasValue || capacity.Value <= QrLineOverheadWorstCase)
            {
                if (_configuredVersion.HasValue)
                {
                    var failedVersion = _configuredVersion.Value;
                    _configuredVersion = null;

                    if (!_versionFallbackMessageShown)
                    {
                        warningMessage =
                            $"QRバージョン {failedVersion} の設定ではデータを格納できません。自動サイズに戻して再生成します。";
                        _versionFallbackMessageShown = true;
                    }

                    var autoCapacity = TryGetMaxQrCapacity(_effectiveEccLevel, -1);
                    if (autoCapacity.HasValue && autoCapacity.Value > QrLineOverheadWorstCase)
                    {
                        return Math.Max(1, autoCapacity.Value - QrLineOverheadWorstCase);
                    }
                }

                return DefaultPayloadLength;
            }

            return Math.Max(1, capacity.Value - QrLineOverheadWorstCase);
        }

        private static int? TryGetMaxQrCapacity(QRCodeGenerator.ECCLevel eccLevel, int requestedVersion)
        {
            if (requestedVersion != -1 && (requestedVersion < 1 || requestedVersion > 40))
            {
                return null;
            }

            using var generator = new QRCodeGenerator();
            var low = 0;
            var high = MaxQrByteCapacity;
            var best = 0;

            while (low <= high)
            {
                var mid = (low + high) / 2;
                QRCodeData? data = null;

                try
                {
                    data = generator.CreateQrCode(new string('A', mid), eccLevel,
                        forceUtf8: true, utf8BOM: false, EciMode.Utf8,
                        requestedVersion: requestedVersion);
                    best = mid;
                    low = mid + 1;
                }
                catch (DataTooLongException)
                {
                    high = mid - 1;
                }
                finally
                {
                    data?.Dispose();
                }
            }

            return best;
        }

        private void UpdateCounterLabel()
        {
            if (_counterLabel == null) return;

            if (_lines.Length == 0)
            {
                _counterLabel.Text = "0 / 0";
            }
            else
            {
                _counterLabel.Text = $"{_idx + 1} / {_lines.Length}";
            }

        }
    }
}
