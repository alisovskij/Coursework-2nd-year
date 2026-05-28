using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebCrawler
{
    /// <summary>
    /// Главное окно приложения. Позволяет настроить параметры обхода,
    /// запустить/остановить краулер и наблюдать результаты в реальном времени.
    /// </summary>
    public class MainForm : Form
    {
        // --- Поля ввода настроек ---
        private readonly TextBox _urlBox = new();
        private readonly NumericUpDown _depthBox = new();
        private readonly NumericUpDown _pagesBox = new();
        private readonly NumericUpDown _delayBox = new();
        private readonly CheckBox _domainBox = new();
        private readonly TextBox _outputBox = new();
        private readonly Button _browseBtn = new();

        // --- Управление ---
        private readonly Button _startBtn = new();
        private readonly Button _stopBtn = new();

        // --- Отображение ---
        private readonly DataGridView _grid = new();
        private readonly TextBox _logBox = new();
        private readonly SplitContainer _split = new();
        private readonly ToolStripProgressBar _progress = new();
        private readonly ToolStripStatusLabel _statusLabel = new();

        private CancellationTokenSource? _cts;

        // Палитра
        private static readonly Color Accent = Color.FromArgb(0, 120, 215);   // синий
        private static readonly Color Danger = Color.FromArgb(196, 43, 28);   // красный
        private static readonly Color PanelBg = Color.FromArgb(245, 246, 248);

        public MainForm()
        {
            Text = "Web Crawler — обход сайтов";
            Width = 1040;
            Height = 720;
            MinimumSize = new Size(820, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildLayout();
            Load += (_, _) => _split.SplitterDistance = (int)(_split.Height * 0.62);
        }

        /// <summary>
        /// Создаёт и размещает все элементы управления.
        /// Корневой TableLayoutPanel задаёт четыре горизонтальные зоны:
        /// настройки, кнопки, результаты, строка состояния.
        /// </summary>
        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12, 12, 12, 0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // настройки
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // кнопки
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // результаты
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // статус

            root.Controls.Add(BuildSettingsGroup(), 0, 0);
            root.Controls.Add(BuildToolbar(), 0, 1);
            root.Controls.Add(BuildResultsArea(), 0, 2);

            Controls.Add(root);
            Controls.Add(BuildStatusBar()); // StatusStrip докуется к низу формы сам
        }

        /// <summary>Группа настроек с выровненной сеткой подпись/поле.</summary>
        private GroupBox BuildSettingsGroup()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 4,
                Padding = new Padding(10, 8, 10, 10),
                AutoSize = true
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // подпись
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));  // поле
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // подпись
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));  // поле
            for (int i = 0; i < 4; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Строка 0 — URL на всю ширину
            grid.Controls.Add(MakeLabel("Стартовый URL:"), 0, 0);
            _urlBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _urlBox.Margin = new Padding(3, 6, 3, 6);
            _urlBox.Text = "https://books.toscrape.com";
            grid.Controls.Add(_urlBox, 1, 0);
            grid.SetColumnSpan(_urlBox, 3);

            // Строка 1 — глубина / макс. страниц
            grid.Controls.Add(MakeLabel("Глубина обхода:"), 0, 1);
            ConfigSpinner(_depthBox, 0, 20, 3);
            grid.Controls.Add(_depthBox, 1, 1);

            grid.Controls.Add(MakeLabel("Макс. страниц:"), 2, 1);
            ConfigSpinner(_pagesBox, 1, 100000, 50);
            grid.Controls.Add(_pagesBox, 3, 1);

            // Строка 2 — задержка / чекбокс домена
            grid.Controls.Add(MakeLabel("Задержка, мс:"), 0, 2);
            ConfigSpinner(_delayBox, 0, 10000, 500);
            _delayBox.Increment = 100;
            grid.Controls.Add(_delayBox, 1, 2);

            _domainBox.Text = "Оставаться на исходном домене";
            _domainBox.Checked = true;
            _domainBox.AutoSize = true;                 // ← текст больше не обрезается
            _domainBox.Anchor = AnchorStyles.Left;
            _domainBox.Margin = new Padding(3, 8, 3, 6);
            grid.Controls.Add(_domainBox, 2, 2);
            grid.SetColumnSpan(_domainBox, 2);

            // Строка 3 — файл вывода + кнопка обзора
            grid.Controls.Add(MakeLabel("Файл вывода:"), 0, 3);
            _outputBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _outputBox.Margin = new Padding(3, 6, 3, 6);
            _outputBox.Text = "crawl_results.txt";
            grid.Controls.Add(_outputBox, 1, 3);
            grid.SetColumnSpan(_outputBox, 2);

            _browseBtn.Text = "Обзор…";
            _browseBtn.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _browseBtn.Height = 28;
            _browseBtn.Margin = new Padding(3, 5, 3, 6);
            _browseBtn.FlatStyle = FlatStyle.System;
            _browseBtn.Click += OnBrowse;
            grid.Controls.Add(_browseBtn, 3, 3);

            var box = new GroupBox
            {
                Text = "Параметры обхода",
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(6, 4, 6, 6),
                Margin = new Padding(0, 0, 0, 10)
            };
            box.Controls.Add(grid);
            return box;
        }

        /// <summary>Панель с кнопками Старт/Стоп одинакового размера.</summary>
        private FlowLayoutPanel BuildToolbar()
        {
            StyleActionButton(_startBtn, "▶  Старт", Accent);
            _startBtn.Click += OnStart;

            StyleActionButton(_stopBtn, "■  Стоп", Danger);
            _stopBtn.Enabled = false;
            _stopBtn.Click += OnStop;

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            panel.Controls.Add(_startBtn);
            panel.Controls.Add(_stopBtn);
            return panel;
        }

        /// <summary>Область результатов: таблица сверху, лог снизу.</summary>
        private SplitContainer BuildResultsArea()
        {
            // Таблица результатов
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.BorderStyle = BorderStyle.None;
            _grid.BackgroundColor = Color.White;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _grid.ColumnHeadersHeight = 32;
            _grid.RowTemplate.Height = 26;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = PanelBg;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(60, 60, 60);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.5f);
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);

            AddColumn("num", "#", 6, DataGridViewContentAlignment.MiddleRight);
            AddColumn("status", "Код", 8, DataGridViewContentAlignment.MiddleCenter);
            AddColumn("depth", "Глуб.", 8, DataGridViewContentAlignment.MiddleCenter);
            AddColumn("title", "Заголовок", 30, DataGridViewContentAlignment.MiddleLeft);
            AddColumn("url", "URL", 32, DataGridViewContentAlignment.MiddleLeft);
            AddColumn("size", "Размер", 8, DataGridViewContentAlignment.MiddleRight);
            AddColumn("links", "Ссылки", 8, DataGridViewContentAlignment.MiddleRight);

            // Лог
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Dock = DockStyle.Fill;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.BackColor = Color.FromArgb(30, 30, 30);
            _logBox.ForeColor = Color.FromArgb(0, 220, 130);
            _logBox.Font = new Font("Consolas", 9.5f);

            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(30, 30, 30) };
            logHost.Controls.Add(_logBox);

            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.Panel1MinSize = 150;
            _split.Panel2MinSize = 80;
            _split.SplitterWidth = 6;
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(logHost);
            return _split;
        }

        /// <summary>Строка состояния с индикатором прогресса.</summary>
        private StatusStrip BuildStatusBar()
        {
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Text = "Готов к работе.";

            _progress.Width = 220;
            _progress.Style = ProgressBarStyle.Continuous;

            var strip = new StatusStrip { SizingGrip = false };
            strip.Items.Add(_statusLabel);
            strip.Items.Add(_progress);
            return strip;
        }

        // ---------- Вспомогательные построители ----------

        private static Label MakeLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,          // вертикальное центрирование в ячейке
            Margin = new Padding(3, 9, 8, 6),
            ForeColor = Color.FromArgb(50, 50, 50)
        };

        private static void ConfigSpinner(NumericUpDown box, int min, int max, int value)
        {
            box.Minimum = min;
            box.Maximum = max;
            box.Value = value;
            box.Width = 110;
            box.Anchor = AnchorStyles.Left;      // вертикальное центрирование
            box.Margin = new Padding(3, 6, 3, 6);
            box.TextAlign = HorizontalAlignment.Right;
        }

        private static void StyleActionButton(Button btn, string text, Color color)
        {
            btn.Text = text;
            btn.Size = new Size(140, 40);
            btn.Margin = new Padding(0, 0, 12, 0);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = color;
            btn.ForeColor = Color.White;
            btn.Font = new Font("Segoe UI Semibold", 10.5f);
            btn.Cursor = Cursors.Hand;
            btn.UseVisualStyleBackColor = false;
        }

        private void AddColumn(string name, string header, int fillWeight, DataGridViewContentAlignment align)
        {
            int idx = _grid.Columns.Add(name, header);
            var col = _grid.Columns[idx];
            col.FillWeight = fillWeight;
            col.DefaultCellStyle.Alignment = align;
            col.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        }

        // ---------- Обработчики ----------

        private void OnBrowse(object? sender, EventArgs e)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
                FileName = _outputBox.Text
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _outputBox.Text = dlg.FileName;
        }

        private async void OnStart(object? sender, EventArgs e)
        {
            string url = _urlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Введите URL.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            var config = new CrawlerConfig
            {
                MaxDepth = (int)_depthBox.Value,
                MaxPages = (int)_pagesBox.Value,
                DelayMs = (int)_delayBox.Value,
                StayOnDomain = _domainBox.Checked,
                OutputFile = string.IsNullOrWhiteSpace(_outputBox.Text) ? "crawl_results.txt" : _outputBox.Text.Trim()
            };

            SetRunningState(true);
            _grid.Rows.Clear();
            _logBox.Clear();
            _progress.Maximum = config.MaxPages;
            _progress.Value = 0;

            // IProgress автоматически маршалит вызовы в UI-поток
            var pageProgress = new Progress<CrawlUpdate>(OnPageCrawled);
            var statusProgress = new Progress<string>(Log);

            _cts = new CancellationTokenSource();
            using var crawler = new Crawler(config);

            try
            {
                await crawler.CrawlAsync(url, pageProgress, statusProgress, _cts.Token);
                _statusLabel.Text = $"Готово. Обработано страниц: {crawler.Results.Count}.";
            }
            catch (OperationCanceledException)
            {
                Log("[INFO] Обход остановлен пользователем.");
                _statusLabel.Text = "Остановлено.";
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                _statusLabel.Text = "Ошибка.";
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunningState(false);
            }
        }

        private void OnStop(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            _stopBtn.Enabled = false;
        }

        /// <summary>Добавляет строку в таблицу при обходе очередной страницы.</summary>
        private void OnPageCrawled(CrawlUpdate update)
        {
            var p = update.Page;
            int rowIndex = _grid.Rows.Add(
                update.TotalCount, p.StatusCode, p.Depth,
                p.Title, p.Url, p.ContentLength, p.LinksFound);

            if (p.StatusCode != 200)
                _grid.Rows[rowIndex].DefaultCellStyle.BackColor = Color.MistyRose;

            // Прокрутка к последней добавленной строке
            _grid.CurrentCell = _grid.Rows[rowIndex].Cells[0];

            if (_progress.Value < _progress.Maximum)
                _progress.Value = Math.Min(update.TotalCount, _progress.Maximum);
            _statusLabel.Text = $"Обработано: {update.TotalCount} | текущая глубина: {p.Depth}";
        }

        private void Log(string message) => _logBox.AppendText(message + Environment.NewLine);

        /// <summary>Переключает доступность элементов на время работы краулера.</summary>
        private void SetRunningState(bool running)
        {
            _startBtn.Enabled = !running;
            _stopBtn.Enabled = running;
            _startBtn.BackColor = running ? Color.FromArgb(160, 200, 240) : Accent;
            _urlBox.Enabled = _depthBox.Enabled = _pagesBox.Enabled =
                _delayBox.Enabled = _domainBox.Enabled = _outputBox.Enabled =
                _browseBtn.Enabled = !running;
        }
    }
}
