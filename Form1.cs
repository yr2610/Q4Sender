using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using QRCoder;
using static QRCoder.QRCodeGenerator;
using Q4Sender.Core;

namespace Q4Sender
{
    public partial class Form1 : Form
    {
        // ---- 状態 ----
        private const int DefaultPayloadLength = 700;
        private const int MaxQrByteCapacity = 2953; // Version40-L（Byteモード）の上限
        private const int QrLineOverheadWorstCase = 17; // "Q4|FFFF/FFFF|SID|" の最大オーバーヘッド
        private const int DefaultTimerInterval = 125;

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
        private readonly System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _helpAutoHide = new System.Windows.Forms.Timer { Interval = 4000 };

        // UI
        private PictureBox _pictureBox;
        private Panel _helpOverlay;
        private Label _helpLabel;
        private Label _counterLabel;
        private Label _skipInfoLabel;
        private TrackBar _seekBar;
        private bool _suppressSeekEvent;
        private TextBox _skipCodeTextBox;
        private Button _skipApplyButton;
        private readonly Queue<int> _scheduledIndices = new();
        private int _scheduledTotal;
        private int _scheduledSent;
        private List<(int start, int end)> _scheduledBuckets = new();
        private bool _skipScheduleRequested;

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

            _timer = new System.Windows.Forms.Timer
            {
                Interval = ResolveTimerInterval(_config.TimerInterval)
            };

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
            AllowDrop = true;

            // いったん Designer のコントロールをクリア（クリーンに構成）
            Controls.Clear();

            // レイアウト全体（QRとカウンタ行）
            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 3,
                Dock = DockStyle.Fill,
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

            // シークバー
            _seekBar = new TrackBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 0,
                TickStyle = TickStyle.None,
                Enabled = false,
                Margin = new Padding(20, 5, 20, 0),
                SmallChange = 1,
                LargeChange = 10,
            };
            _seekBar.Scroll += SeekBar_Scroll;
            layout.Controls.Add(_seekBar, 0, 1);

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
  Drag&Drop : ファイルを開く
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
            layout.Controls.Add(counterPanel, 0, 2);

            var skipPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10, 0, 0, 0),
                Margin = new Padding(0),
            };
            counterPanel.Controls.Add(skipPanel);

            var skipLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Text = "Skip",
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 2, 6, 0),
            };
            skipPanel.Controls.Add(skipLabel);

            _skipCodeTextBox = new TextBox
            {
                Width = 120,
                MaxLength = 7,
                CharacterCasing = CharacterCasing.Lower,
                Margin = new Padding(0, 0, 6, 0),
            };
            _skipCodeTextBox.KeyDown += SkipCodeTextBox_KeyDown;
            skipPanel.Controls.Add(_skipCodeTextBox);

            _skipApplyButton = new Button
            {
                Text = "適用",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 6, 0),
            };
            _skipApplyButton.Click += (s, e) => ApplySkipCode();
            skipPanel.Controls.Add(_skipApplyButton);

            _skipInfoLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Text = "Skip: 全件",
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 2, 0, 0),
            };
            skipPanel.Controls.Add(_skipInfoLabel);

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

            DragEnter += Form1_DragEnter;
            DragDrop += Form1_DragDrop;

            UpdateSeekBarState();
            UpdateSkipInfoLabel();
        }

        private static int ResolveTimerInterval(int? configuredInterval)
        {
            return configuredInterval is int value && value >= 1
                ? value
                : DefaultTimerInterval;
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

            LoadFromPath(ofd.FileName);
        }

        private void LoadFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

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
                int payloadLen;
                string? configWarning = null;

                try
                {
                    payloadLen = DeterminePayloadLength(out configWarning);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "設定の読み込みに失敗しました: " + ex.Message, "Q4Sender",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!TryEnsureFitsFrameLimit(path, payloadLen, out var sizeWarning))
                {
                    if (!string.IsNullOrEmpty(sizeWarning))
                    {
                        MessageBox.Show(this, sizeWarning, "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                try
                {
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
                        //var outTxt = Path.ChangeExtension(path, ".q4.txt");
                        //File.WriteAllLines(outTxt, _lines, new UTF8Encoding(false));
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

            ClearSkipSchedule();
            _idx = 0;
            _paused = false;
            _timer.Start();
            ShowCurrent();
        }

        private bool TryEnsureFitsFrameLimit(string filePath, int payloadLen, out string? warningMessage)
        {
            warningMessage = null;

            if (payloadLen <= 0)
            {
                warningMessage = "QR コードのペイロード長が不正です。";
                return false;
            }

            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists)
                {
                    warningMessage = "ファイルが存在しません。";
                    return false;
                }

                var fileLength = fi.Length;

                // zip圧縮+Base64 化後のサイズを安全側に見積もる（余裕を持った上限）
                var inflatedEstimate = (double)fileLength * 2.0 + 1024.0; // 安全側に多めに見積
                if (inflatedEstimate < 0) inflatedEstimate = double.MaxValue / 2; // 念のため

                var base64Length = Math.Ceiling(inflatedEstimate / 3.0) * 4.0;
                var estimatedFrames = double.IsInfinity(base64Length)
                    ? (long)0x10000
                    : (long)Math.Ceiling(base64Length / payloadLen);

                if (estimatedFrames > 0xFFFF)
                {
                    warningMessage =
                        $"ファイルサイズが大きすぎるため中止しました。\n推定 {estimatedFrames:N0} 枚が必要ですが、上限は 65,535 枚です。";
                    return false;
                }
            }
            catch (Exception ex)
            {
                warningMessage = "ファイルサイズの確認に失敗しました: " + ex.Message;
                return false;
            }

            return true;
        }

        // ========= 表示制御 =========
        private void ShowNext()
        {
            if (_lines.Length == 0) return;
            if (TryProcessNextScheduled(out int scheduledIndex))
            {
                _idx = scheduledIndex;
                ShowCurrent();
                return;
            }

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
                UpdateSeekBarState();
                UpdateSkipInfoLabel();
                return;
            }

            if (_invalidEccValue != null && !_eccWarningShown)
            {
                _eccWarningShown = true; // 警告ダイアログが多重に開かないように先にフラグを更新
                MessageBox.Show(this,
                    $"QR誤り訂正レベルの設定値 '{_invalidEccValue}' は無効です。既定値(Q)を使用します。",
                    "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                catch (QRCoder.Exceptions.DataTooLongException) when (_configuredVersion != null)
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
                    UpdateSeekBarState();
                    UpdateSkipInfoLabel();
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
                catch (QRCoder.Exceptions.DataTooLongException)
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

        private void UpdateSeekBarState()
        {
            if (_seekBar == null) return;

            _suppressSeekEvent = true;

            if (_lines.Length == 0)
            {
                _seekBar.Enabled = false;
                _seekBar.Minimum = 0;
                _seekBar.Maximum = 0;
                _seekBar.Value = 0;
            }
            else
            {
                _seekBar.Enabled = true;
                _seekBar.Minimum = 0;
                var newMax = Math.Max(0, _lines.Length - 1);
                if (_seekBar.Value > newMax)
                {
                    _seekBar.Value = newMax;
                }
                _seekBar.Maximum = newMax;
                var newValue = Math.Max(_seekBar.Minimum, Math.Min(newMax, _idx));
                if (_seekBar.Value != newValue)
                {
                    _seekBar.Value = newValue;
                }
            }

            _suppressSeekEvent = false;
        }

        private void SeekBar_Scroll(object? sender, EventArgs e)
        {
            if (_suppressSeekEvent || _seekBar == null) return;

            var newIndex = _seekBar.Value;
            if (newIndex == _idx || _lines.Length == 0)
            {
                return;
            }

            _idx = newIndex;
            ShowCurrent();
        }

        private void SkipCodeTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ApplySkipCode();
            }
        }

        private void ApplySkipCode()
        {
            if (_skipCodeTextBox == null)
            {
                return;
            }

            if (_lines.Length == 0)
            {
                MessageBox.Show(this, "コンテンツが読み込まれていません。", "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var code = _skipCodeTextBox.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                ClearSkipSchedule();
                return;
            }

            if (!SkipCode32H5T2.TryDecode(code, out var mask))
            {
                MessageBox.Show(this, "SkipCode の解読に失敗しました。", "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var buckets = SkipCode32H5T2.Buckets(mask, _lines.Length).ToList();
            var queue = new Queue<int>();
            foreach (var (start, end) in buckets)
            {
                var clampedStart = Math.Max(0, start);
                var clampedEnd = Math.Min(_lines.Length - 1, end);
                if (clampedStart > clampedEnd)
                {
                    continue;
                }

                for (int i = clampedStart; i <= clampedEnd; i++)
                {
                    queue.Enqueue(i);
                }
            }

            _scheduledIndices.Clear();
            foreach (var index in queue)
            {
                _scheduledIndices.Enqueue(index);
            }

            _scheduledBuckets = buckets;
            _scheduledTotal = _scheduledIndices.Count;
            _scheduledSent = 0;
            _skipScheduleRequested = true;

            UpdateSkipInfoLabel();

            if (_scheduledTotal == 0)
            {
                MessageBox.Show(this, "対象となるバケツがありません。", "Q4Sender", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (TryProcessNextScheduled(out int firstIndex))
            {
                _idx = firstIndex;
                ShowCurrent();
            }
        }

        private bool TryProcessNextScheduled(out int index)
        {
            index = -1;
            if (_scheduledIndices.Count == 0)
            {
                return false;
            }

            index = _scheduledIndices.Dequeue();
            _scheduledSent++;
            return true;
        }

        private void ClearSkipSchedule()
        {
            _scheduledIndices.Clear();
            _scheduledTotal = 0;
            _scheduledSent = 0;
            _scheduledBuckets = new();
            _skipScheduleRequested = false;
            UpdateSkipInfoLabel();
        }

        private void UpdateSkipInfoLabel()
        {
            if (_skipInfoLabel == null)
            {
                return;
            }

            if (_lines.Length == 0)
            {
                _skipInfoLabel.Text = "Skip: 0/0";
                return;
            }

            if (!_skipScheduleRequested)
            {
                _skipInfoLabel.Text = "Skip: 全件";
                return;
            }

            if (_scheduledTotal == 0)
            {
                var noneText = FormatBucketRanges();
                _skipInfoLabel.Text = $"Skip: 対象なし ({noneText})";
                return;
            }

            var remaining = Math.Max(0, _scheduledTotal - _scheduledSent);
            var ranges = FormatBucketRanges();
            if (remaining == 0)
            {
                _skipInfoLabel.Text = $"Skip: 完了 {_scheduledTotal}/{_scheduledTotal} ({ranges})";
            }
            else
            {
                _skipInfoLabel.Text = $"Skip: 残り {remaining}/{_scheduledTotal} ({ranges})";
            }
        }

        private string FormatBucketRanges()
        {
            if (_scheduledBuckets == null || _scheduledBuckets.Count == 0)
            {
                return "-";
            }

            return string.Join(", ", _scheduledBuckets.Select(bucket =>
            {
                int displayStart = bucket.start + 1;
                int displayEnd = bucket.end + 1;
                return displayStart == displayEnd
                    ? displayStart.ToString()
                    : $"{displayStart}-{displayEnd}";
            }));
        }

        private void Form1_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                LoadFromPath(files[0]);
            }
        }
    }
}
