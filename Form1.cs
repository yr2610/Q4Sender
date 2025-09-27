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
        // ---- ��� ----
        private string[] _lines = Array.Empty<string>();
        private int _idx = 0;
        private bool _paused = false;
        private bool _fullscreen = false;

        // �^�C�}
        private System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private System.Windows.Forms.Timer _helpAutoHide = new System.Windows.Forms.Timer { Interval = 4000 };

        // UI
        private PictureBox _pictureBox;
        private Panel _helpOverlay;
        private Label _helpLabel;

        public Form1()
        {
            InitializeComponent(); // Designer �L���ǂ���ł�OK�i��ŏ㏑���j
            Text = "Q4Sender";
            StartPosition = FormStartPosition.CenterScreen;

            // ==== �����T�C�Y�F�����߁i�ȑO�� 900x700 ��� ����ɏ������j====
            Width = 480; Height = 360;  // ���v�]�ʂ�A�����ȉ��̃T�C�Y��

            KeyPreview = true;

            // �������� Designer �̃R���g���[�����N���A�i�N���[���ɍ\���j
            Controls.Clear();

            // �摜�\���̈�
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            Controls.Add(_pictureBox);
            _pictureBox.BringToFront();

            // �w���v�I�[�o�[���C�i�������j
            _helpOverlay = new Panel
            {
                BackColor = Color.FromArgb(180, 15, 23, 42), // �������_�[�N�i#0F172A�j
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
@"����:
  Ctrl+O : �t�@�C�����J���i�C�Ӄt�@�C�� or Q4�s�e�L�X�g�j
  Space  : �ꎞ��~/�ĊJ
  F      : �S��ʐؑ�
  �� / ��  : �O / ��
  Esc    : �I��
  F1     : �w���v�\��"
            };
            _helpOverlay.Controls.Add(_helpLabel);
            Controls.Add(_helpOverlay);

            // �z�u�i����ɏ����]�����󂯂�j
            _helpOverlay.Left = 10;
            _helpOverlay.Top = 10;
            _helpOverlay.BringToFront();
            _helpOverlay.Visible = true; // �N�����͕\��

            // ������\���^�C�}�i4�b�j
            _helpAutoHide.Tick += (s, e) => { _helpAutoHide.Stop(); _helpOverlay.Visible = false; };
            _helpAutoHide.Start();

            // ������ጸ
            this.DoubleBuffered = true;
            this.BackColor = Color.White;

            // �C�x���g
            _timer.Tick += (s, e) => ShowNext();
            KeyDown += Form1_KeyDown;

            Shown += (s, e) =>
            {
                _pictureBox.BackColor = Color.White;
            };
        }

        // ========= �L�[���� =========
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Space)
            {
                _paused = !_paused;
                if (_paused) _timer.Stop(); else _timer.Start();
            }
            else if (e.Control && e.KeyCode == Keys.O) LoadAny();         // �C�Ӄt�@�C�� or Q4�e�L�X�g
            else if (e.KeyCode == Keys.F) ToggleFullScreen();             // �S���
            else if (e.KeyCode == Keys.Right) ShowNext();                 // ����
            else if (e.KeyCode == Keys.Left) ShowPrev();                  // �O��
            else if (e.KeyCode == Keys.F1) ShowHelpOverlay();             // �w���v�ĕ\��
        }

        private void ShowHelpOverlay()
        {
            _helpOverlay.Visible = true;
            _helpOverlay.BringToFront();
            _helpAutoHide.Stop();
            _helpAutoHide.Start(); // 4�b��Ɏ����ŏ�����
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

        // ========= �ǂݍ��݁i�C�Ӄt�@�C�� or ����Q4�e�L�X�g�j =========
        private void LoadAny()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "���ׂẴt�@�C��|*.*",
                Title = "���M����t�@�C����I���i�C�Ӂj�^�܂���Q4�e�L�X�g��I��"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            var path = ofd.FileName;

            // �܂��uQ4�e�L�X�g�v�Ƃ��ēǂ߂邩�y������
            string[] asTextQ4 = Array.Empty<string>();
            try
            {
                var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
                asTextQ4 = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.StartsWith("Q4|", StringComparison.OrdinalIgnoreCase))
                               .ToArray();
            }
            catch { /* �o�C�i�����Ŏ��s���Ă�OK */ }

            if (asTextQ4.Length > 0)
            {
                _lines = asTextQ4;
                Text = $"Q4Sender - ����Q4�s {_lines.Length} ��";
            }
            else
            {
                try
                {
                    var (packed, sid) = PackFileToQ4Lines(path, payloadLen: 700);
                    _lines = packed;
                    Text = $"Q4Sender - SID={sid} ���� {_lines.Length} ��";

                    // �������ʂ� .q4.txt �Ƃ��ď����o���i�C�Ӂj
                    try
                    {
                        var outTxt = Path.ChangeExtension(path, ".q4.txt");
                        File.WriteAllLines(outTxt, _lines, new UTF8Encoding(false));
                    }
                    catch { /* ���� */ }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "�p�b�L���O�Ɏ��s���܂���: " + ex.Message, "Q4Sender",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            _idx = 0;
            _paused = false;
            _timer.Start();
            ShowCurrent();
        }

        // ========= �\������ =========
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

                // QRCoder �Ő����i������ Q ���� / M �ł��j
                using var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(line, QRCodeGenerator.ECCLevel.Q,
                                            forceUtf8: true, utf8BOM: false, EciMode.Utf8);

                using var qr = new QRCode(data);
                using var bmp = qr.GetGraphic(
                    pixelsPerModule: 16,       // �����߃E�B���h�E�ł����₷���悤�傫��
                    Color.Black,
                    Color.White,
                    drawQuietZones: true);

                _pictureBox.BackColor = Color.White;
                _pictureBox.Image?.Dispose();
                _pictureBox.Image = (Bitmap)bmp.Clone();

                // �^�C�g���͍T���߂Ɂi�d�����Ȃ��j
                // Text = $"Q4Sender - {(_idx + 1)}/{_lines.Length} - {DateTime.Now:T}";
            }
            catch (Exception ex)
            {
                _timer.Stop();
                MessageBox.Show(this, "QR�����E�`��ŃG���[: " + ex.Message, "Q4Sender",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ========= �p�b�J�[�i�C�Ӄt�@�C�� �� Q4�s�j =========

        // Base64URL�i= �Ɩ����p�f�B���O�����j
        private static string Base64UrlNoPad(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return s;
        }

        // 3������ Base36 SID
        private static string MakeSid(int len = 3)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            using var rng = RandomNumberGenerator.Create();
            var buf = new byte[len]; rng.GetBytes(buf);
            var sb = new StringBuilder(len);
            foreach (var b in buf) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        // �C�Ӄt�@�C�� �� gzip �� Base64URL �� �Œ蒷���� �� Q4�s
        private static (string[] lines, string sid) PackFileToQ4Lines(string filePath, int payloadLen = 700, string? sid = null)
        {
            sid ??= MakeSid(3);

            // �ǂݍ��݁i�o�C�i��OK�j
            byte[] raw = File.ReadAllBytes(filePath);

            // gzip���k
            byte[] gz;
            using (var msOut = new MemoryStream())
            {
                using (var gzStream = new GZipStream(msOut, CompressionLevel.Optimal, leaveOpen: true))
                    gzStream.Write(raw, 0, raw.Length);
                gz = msOut.ToArray();
            }

            // Base64URL�i�p�f�B���O���j
            var b64u = Base64UrlNoPad(gz);

            // ����
            var parts = Enumerable.Range(0, (int)Math.Ceiling(b64u.Length / (double)payloadLen))
                                  .Select(i => b64u.Substring(i * payloadLen, Math.Min(payloadLen, b64u.Length - i * payloadLen)))
                                  .ToArray();
            int tot = parts.Length;
            if (tot < 1 || tot > 0xFFFF) throw new InvalidOperationException("���������͈͊O�ł�");

            // Q4�s�iidx/tot ��16�i�j
            var lines = parts.Select((p, i) => $"Q4|{(i + 1).ToString("X")}/{tot.ToString("X")}|{sid}|{p}")
                             .ToArray();

            return (lines, sid);
        }
    }
}
