using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PlcsimGateway.Gui
{
    public sealed class MainForm : Form
    {
        private readonly GatewayController controller;
        private readonly Timer refreshTimer;
        private const int ActionTimeoutMilliseconds = 20000;
        private const int WmSetRedraw = 0x000B;
        private const int EmGetScrollPos = 0x04DD;
        private const int EmSetScrollPos = 0x04DE;
        private const string HelpResourceRu = "PlcsimGateway.Gui.Help.ru.md";
        private const string HelpResourceEn = "PlcsimGateway.Gui.Help.en.md";
        private static readonly HelpTextSections EmbeddedHelpText = LoadEmbeddedHelpText();
        private bool formReady;
        private bool refreshInProgress;
        private bool actionInProgress;
        private bool loadingProfileFields;
        private GatewayStatus lastStatus;
        private GatewayRuntimeStatus lastRuntimeStatus;
        private ComboBox profileComboBox;
        private TextBox listenAddressTextBox;
        private TextBox plcsimAddressTextBox;
        private Button saveProfileButton;
        private Button refreshButton;
        private Button startButton;
        private Button stopButton;
        private Button restartButton;
        private CheckBox autoRefreshCheckBox;
        private Label stateValueLabel;
        private Label listenValueLabel;
        private Label plcsimValueLabel;
        private Label pidValueLabel;
        private Label sessionsValueLabel;
        private Label logValueLabel;
        private Label healthValueLabel;
        private Label runtimeValueLabel;
        private Label summaryValueLabel;
        private RichTextBox profileHelpRuTextBox;
        private RichTextBox profileHelpEnTextBox;
        private DataGridView activeSessionsGrid;
        private DataGridView disconnectsGrid;
        private TextBox logTextBox;
        private TextBox outputTextBox;

        public MainForm()
        {
            controller = new GatewayController(FindRepoRoot());
            refreshTimer = new Timer();
            refreshTimer.Interval = 3000;
            refreshTimer.Tick += async delegate { await RefreshStatusAsync(false); };

            InitializeComponent();
            LoadProfiles();
            Shown += async delegate
            {
                formReady = true;
                await RefreshStatusAsync(false);
                refreshTimer.Enabled = autoRefreshCheckBox.Checked;
            };
            FormClosing += delegate
            {
                refreshTimer.Enabled = false;
                controller.Dispose();
            };
        }

        private void InitializeComponent()
        {
            Text = "PlcsimGateway Control";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1120, 680);
            Size = new Size(1240, 800);
            WindowState = FormWindowState.Maximized;
            Icon applicationIcon = LoadApplicationIcon();
            if (applicationIcon != null)
            {
                Icon = applicationIcon;
            }

            TabControl mainTabs = new TabControl();
            mainTabs.Dock = DockStyle.Fill;
            mainTabs.Padding = new Point(12, 4);

            TabPage workPage = new TabPage("Work");
            workPage.Controls.Add(CreateWorkPage());

            TabPage helpPage = new TabPage("Help");
            helpPage.Padding = new Padding(8);
            helpPage.Controls.Add(CreateHelpPage());

            mainTabs.TabPages.Add(workPage);
            mainTabs.TabPages.Add(helpPage);
            Controls.Add(mainTabs);
        }

        private Control CreateWorkPage()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));

            root.Controls.Add(CreateToolbar(), 0, 0);
            root.Controls.Add(CreateStatusPanel(), 0, 1);
            root.Controls.Add(CreateSessionTabs(), 0, 2);
            root.Controls.Add(CreateLogTextBox(), 0, 3);
            root.Controls.Add(CreateOutputTextBox(), 0, 4);
            return root;
        }

        private Control CreateHelpPage()
        {
            TableLayoutPanel helpLayout = new TableLayoutPanel();
            helpLayout.Dock = DockStyle.Fill;
            helpLayout.ColumnCount = 2;
            helpLayout.RowCount = 1;
            helpLayout.Padding = new Padding(8, 6, 8, 8);
            helpLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            helpLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            helpLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            GroupBox ruGroup = CreateHelpGroup("RU - \u0420\u0443\u0441\u0441\u043a\u0438\u0439");
            GroupBox enGroup = CreateHelpGroup("EN - English");
            profileHelpRuTextBox = CreateProfileHelpTextBox();
            profileHelpEnTextBox = CreateProfileHelpTextBox();
            ruGroup.Controls.Add(CreateHelpTextHost(profileHelpRuTextBox));
            enGroup.Controls.Add(CreateHelpTextHost(profileHelpEnTextBox));
            helpLayout.Controls.Add(ruGroup, 0, 0);
            helpLayout.Controls.Add(enGroup, 1, 0);
            return helpLayout;
        }

        private GroupBox CreateHelpGroup(string title)
        {
            GroupBox group = new GroupBox();
            group.Text = title;
            group.Dock = DockStyle.Fill;
            group.Margin = new Padding(6);
            group.Padding = new Padding(12, 22, 12, 12);
            return group;
        }

        private static Control CreateHelpTextHost(RichTextBox textBox)
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(18, 14, 18, 14);
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.BackColor = Color.FromArgb(247, 251, 255);
            panel.Controls.Add(textBox);
            return panel;
        }

        private Control CreateToolbar()
        {
            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.FlowDirection = FlowDirection.LeftToRight;
            toolbar.Padding = new Padding(8, 8, 8, 4);
            toolbar.WrapContents = false;

            profileComboBox = new ComboBox();
            profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            profileComboBox.Width = 220;
            profileComboBox.SelectedIndexChanged += async delegate
            {
                lastStatus = null;
                lastRuntimeStatus = null;
                LoadSelectedProfileFields();
                await RefreshStatusAsync(false);
            };

            listenAddressTextBox = CreateToolbarTextBox();
            plcsimAddressTextBox = CreateToolbarTextBox();
            listenAddressTextBox.TextChanged += ProfileAddressTextBox_TextChanged;
            plcsimAddressTextBox.TextChanged += ProfileAddressTextBox_TextChanged;
            saveProfileButton = CreateButton("Save IPs", SaveProfileButton_Click);

            refreshButton = CreateButton("Refresh", RefreshButton_Click);
            startButton = CreateButton("Start", StartButton_Click);
            stopButton = CreateButton("Stop", StopButton_Click);
            restartButton = CreateButton("Restart", RestartButton_Click);

            autoRefreshCheckBox = new CheckBox();
            autoRefreshCheckBox.Text = "Auto refresh";
            autoRefreshCheckBox.Checked = true;
            autoRefreshCheckBox.AutoSize = true;
            autoRefreshCheckBox.Margin = new Padding(12, 8, 4, 4);
            autoRefreshCheckBox.CheckedChanged += delegate { refreshTimer.Enabled = autoRefreshCheckBox.Checked; };

            toolbar.Controls.Add(profileComboBox);
            toolbar.Controls.Add(CreateToolbarLabel("Network IP"));
            toolbar.Controls.Add(listenAddressTextBox);
            toolbar.Controls.Add(CreateToolbarLabel("PLCSIM IP"));
            toolbar.Controls.Add(plcsimAddressTextBox);
            toolbar.Controls.Add(saveProfileButton);
            toolbar.Controls.Add(refreshButton);
            toolbar.Controls.Add(startButton);
            toolbar.Controls.Add(stopButton);
            toolbar.Controls.Add(restartButton);
            toolbar.Controls.Add(autoRefreshCheckBox);
            return toolbar;
        }

        private Label CreateToolbarLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(8, 9, 0, 4);
            return label;
        }

        private TextBox CreateToolbarTextBox()
        {
            TextBox textBox = new TextBox();
            textBox.Width = 110;
            textBox.Margin = new Padding(4, 5, 0, 4);
            return textBox;
        }

        private Button CreateButton(string text, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 92;
            button.Height = 28;
            button.Margin = new Padding(6, 4, 0, 4);
            button.Click += handler;
            return button;
        }

        private Control CreateStatusPanel()
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 4;
            panel.RowCount = 5;
            panel.Padding = new Padding(12, 4, 12, 4);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            stateValueLabel = AddField(panel, "State", 0, 0);
            listenValueLabel = AddField(panel, "Network", 0, 1);
            plcsimValueLabel = AddField(panel, "PLCSIM", 0, 2);
            pidValueLabel = AddField(panel, "PID", 2, 0);
            sessionsValueLabel = AddField(panel, "Sessions", 2, 1);
            logValueLabel = AddField(panel, "Log", 2, 2);
            healthValueLabel = AddField(panel, "Health", 0, 3);
            runtimeValueLabel = AddField(panel, "Runtime", 2, 3);
            summaryValueLabel = AddField(panel, "Summary", 0, 4);
            panel.SetColumnSpan(summaryValueLabel, 3);

            return panel;
        }

        private Label AddField(TableLayoutPanel panel, string caption, int column, int row)
        {
            Label captionLabel = new Label();
            captionLabel.Text = caption;
            captionLabel.Dock = DockStyle.Fill;
            captionLabel.TextAlign = ContentAlignment.MiddleLeft;
            captionLabel.Font = new Font(Font, FontStyle.Bold);

            Label valueLabel = new Label();
            valueLabel.Text = "-";
            valueLabel.Dock = DockStyle.Fill;
            valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            valueLabel.AutoEllipsis = true;

            panel.Controls.Add(captionLabel, column, row);
            panel.Controls.Add(valueLabel, column + 1, row);
            return valueLabel;
        }

        private Control CreateSessionTabs()
        {
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(12, 4);

            activeSessionsGrid = CreateSessionGrid();
            disconnectsGrid = CreateSessionGrid();

            TabPage activePage = new TabPage("Active sessions");
            activePage.Controls.Add(activeSessionsGrid);

            TabPage disconnectPage = new TabPage("Last disconnects");
            disconnectPage.Controls.Add(disconnectsGrid);

            tabs.TabPages.Add(activePage);
            tabs.TabPages.Add(disconnectPage);
            return tabs;
        }

        private RichTextBox CreateProfileHelpTextBox()
        {
            RichTextBox textBox = new RichTextBox();
            textBox.Dock = DockStyle.Fill;
            textBox.ReadOnly = true;
            textBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            textBox.WordWrap = true;
            textBox.BorderStyle = BorderStyle.None;
            textBox.BackColor = Color.FromArgb(247, 251, 255);
            textBox.Font = new Font("Segoe UI", 10.5f);
            textBox.DetectUrls = false;
            return textBox;
        }

        private DataGridView CreateSessionGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.FixedSingle;

            AddGridColumn(grid, "Id", "SessionId", 60);
            AddGridColumn(grid, "Remote", "Remote", 145);
            AddGridColumn(grid, "Duration", "Duration", 85);
            AddGridColumn(grid, "Protocol", "Protocol", 90);
            AddGridColumn(grid, "Client PDU", "ClientPdus", 80);
            AddGridColumn(grid, "PLCSIM PDU", "PlcsimPdus", 90);
            AddGridColumn(grid, "Client bytes", "ClientBytes", 90);
            AddGridColumn(grid, "PLCSIM bytes", "PlcsimBytes", 95);
            AddGridColumn(grid, "Last event", "LastEvent", 95);
            AddGridColumn(grid, "Last time", "LastEventAt", 140);
            return grid;
        }

        private void AddGridColumn(DataGridView grid, string header, string propertyName, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.DataPropertyName = propertyName;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(column);
        }

        private Control CreateLogTextBox()
        {
            logTextBox = new TextBox();
            logTextBox.Dock = DockStyle.Fill;
            logTextBox.Multiline = true;
            logTextBox.ReadOnly = true;
            logTextBox.ScrollBars = ScrollBars.Both;
            logTextBox.WordWrap = false;
            logTextBox.Font = new Font("Consolas", 9.0f);
            return logTextBox;
        }

        private Control CreateOutputTextBox()
        {
            outputTextBox = new TextBox();
            outputTextBox.Dock = DockStyle.Fill;
            outputTextBox.Multiline = true;
            outputTextBox.ReadOnly = true;
            outputTextBox.ScrollBars = ScrollBars.Vertical;
            outputTextBox.Font = new Font("Consolas", 9.0f);
            return outputTextBox;
        }

        private void LoadProfiles()
        {
            IReadOnlyList<GatewayProfile> profiles = controller.LoadProfiles();
            profileComboBox.Items.Clear();
            foreach (GatewayProfile profile in profiles)
            {
                profileComboBox.Items.Add(profile);
            }

            if (profileComboBox.Items.Count > 0)
            {
                profileComboBox.SelectedIndex = 0;
                LoadSelectedProfileFields();
            }
        }

        private void LoadSelectedProfileFields()
        {
            GatewayProfile profile = GetSelectedProfile();
            if (profile == null || listenAddressTextBox == null || plcsimAddressTextBox == null)
            {
                return;
            }

            loadingProfileFields = true;
            try
            {
                listenAddressTextBox.Text = profile.listenAddress ?? String.Empty;
                plcsimAddressTextBox.Text = profile.plcsimAddress ?? String.Empty;
            }
            finally
            {
                loadingProfileFields = false;
            }

            UpdateProfileHelpText();
        }

        private void ProfileAddressTextBox_TextChanged(object sender, EventArgs e)
        {
            if (!loadingProfileFields)
            {
                UpdateProfileHelpText();
            }
        }

        private async void RefreshButton_Click(object sender, EventArgs e)
        {
            await RefreshStatusAsync(true);
        }

        private async void SaveProfileButton_Click(object sender, EventArgs e)
        {
            await SaveProfileAddressesAsync();
        }

        private async void StartButton_Click(object sender, EventArgs e)
        {
            await RunActionAsync("start", delegate(string profileName) { return controller.Start(profileName); });
        }

        private async void StopButton_Click(object sender, EventArgs e)
        {
            await RunActionAsync("stop", delegate(string profileName) { return controller.Stop(profileName); });
        }

        private async void RestartButton_Click(object sender, EventArgs e)
        {
            await RunActionAsync("restart", delegate(string profileName) { return controller.Restart(profileName); });
        }

        private async Task SaveProfileAddressesAsync()
        {
            if (actionInProgress)
            {
                return;
            }

            GatewayProfile profile = GetSelectedProfile();
            if (profile == null)
            {
                return;
            }

            string listenAddress = listenAddressTextBox.Text.Trim();
            string plcsimAddress = plcsimAddressTextBox.Text.Trim();

            try
            {
                ValidateIpv4Address("Network IP", listenAddress);
                ValidateIpv4Address("PLCSIM IP", plcsimAddress);

                actionInProgress = true;
                refreshTimer.Enabled = false;
                SetBusy(true);
                controller.SaveProfileAddresses(profile.name, listenAddress, plcsimAddress);
                profile.listenAddress = listenAddress;
                profile.plcsimAddress = plcsimAddress;
                UpdateProfileHelpText();
                WriteOutput("profile addresses saved; restart the profile to apply listener changes", null);
            }
            catch (Exception ex)
            {
                WriteError("save profile failed: " + ex.Message);
            }
            finally
            {
                actionInProgress = false;
                SetBusy(false);
                refreshTimer.Enabled = autoRefreshCheckBox.Checked;
                await RefreshStatusAsync(false);
            }
        }

        private static void ValidateIpv4Address(string fieldName, string value)
        {
            IPAddress address;
            if (String.IsNullOrWhiteSpace(value)
                || !IPAddress.TryParse(value, out address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException(fieldName + " must be an IPv4 address.");
            }
        }

        private async Task RunActionAsync(string actionName, Func<string, PowerShellResult> action)
        {
            if (actionInProgress)
            {
                return;
            }

            string profileName = GetSelectedProfileName();
            try
            {
                actionInProgress = true;
                refreshTimer.Enabled = false;
                SetBusy(true);
                WriteOutput(actionName + " started", null);
                Task<PowerShellResult> actionTask = Task.Run(delegate { return action(profileName); });
                Task completedTask = await Task.WhenAny(actionTask, Task.Delay(ActionTimeoutMilliseconds));
                if (completedTask != actionTask)
                {
                    ObserveFault(actionTask);
                    throw new TimeoutException(actionName + " did not complete in " + (ActionTimeoutMilliseconds / 1000) + " seconds.");
                }

                PowerShellResult result = await actionTask;
                WriteOutput(actionName + " completed", result);
            }
            catch (Exception ex)
            {
                WriteError(actionName + " failed: " + ex.Message);
            }
            finally
            {
                actionInProgress = false;
                SetBusy(false);
                refreshTimer.Enabled = autoRefreshCheckBox.Checked;
                await RefreshStatusAsync(false);
            }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(
                delegate(Task completedTask)
                {
                    Exception ignored = completedTask.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task RefreshStatusAsync(bool writeOutput)
        {
            if (!formReady || profileComboBox.SelectedItem == null || refreshInProgress || actionInProgress)
            {
                return;
            }

            try
            {
                refreshInProgress = true;
                string profileName = GetSelectedProfileName();
                StatusViewData viewData = await Task.Run(delegate { return LoadStatusViewData(profileName); });
                ApplyStatus(viewData.Status, viewData.RuntimeStatus, viewData.LogTail);
                if (writeOutput)
                {
                    WriteOutput("status refreshed", null);
                }
            }
            catch (Exception ex)
            {
                lastStatus = null;
                lastRuntimeStatus = null;
                stateValueLabel.Text = "ERROR";
                UpdateProfileHelpText();
                WriteError("status failed: " + ex.Message);
            }
            finally
            {
                refreshInProgress = false;
            }
        }

        private StatusViewData LoadStatusViewData(string profileName)
        {
            GatewayStatus status = controller.GetStatus(profileName);
            GatewayRuntimeStatus runtimeStatus = controller.GetRuntimeStatus(profileName);
            string logTail = status == null ? String.Empty : controller.ReadTail(status.LogPath, 220);
            return new StatusViewData
            {
                Status = status,
                RuntimeStatus = runtimeStatus,
                LogTail = logTail
            };
        }

        private void ApplyStatus(GatewayStatus status, GatewayRuntimeStatus runtimeStatus, string logTail)
        {
            if (status == null)
            {
                lastStatus = null;
                lastRuntimeStatus = null;
                stateValueLabel.Text = "UNKNOWN";
                listenValueLabel.Text = "-";
                plcsimValueLabel.Text = "-";
                pidValueLabel.Text = "-";
                sessionsValueLabel.Text = "-";
                logValueLabel.Text = "-";
                healthValueLabel.Text = "-";
                runtimeValueLabel.Text = "-";
                summaryValueLabel.Text = "-";
                logTextBox.Text = String.Empty;
                BindSessions(activeSessionsGrid, null);
                BindSessions(disconnectsGrid, null);
                UpdateProfileHelpText();
                return;
            }

            bool running = status.ListenerPid.HasValue;
            if (!running)
            {
                runtimeStatus = null;
            }

            lastStatus = status;
            lastRuntimeStatus = runtimeStatus;

            stateValueLabel.Text = running ? "RUNNING" : "STOPPED";
            stateValueLabel.ForeColor = running ? Color.DarkGreen : Color.Firebrick;
            listenValueLabel.Text = status.Listen ?? "-";
            plcsimValueLabel.Text = status.Plcsim ?? "-";
            pidValueLabel.Text = status.ListenerPid.HasValue ? status.ListenerPid.Value.ToString() : "-";
            sessionsValueLabel.Text = status.ClientSessions.ToString();
            logValueLabel.Text = status.LogPath ?? "-";
            healthValueLabel.Text = GetHealthText(status, runtimeStatus);
            runtimeValueLabel.Text = runtimeStatus == null ? "no snapshot" : runtimeStatus.generatedAt;

            logTextBox.Text = logTail;
            logTextBox.SelectionStart = logTextBox.TextLength;
            logTextBox.ScrollToCaret();
            summaryValueLabel.Text = GetSummaryText(runtimeStatus, logTail);
            BindSessions(activeSessionsGrid, runtimeStatus == null ? null : runtimeStatus.activeSessions);
            BindSessions(disconnectsGrid, runtimeStatus == null ? null : runtimeStatus.lastDisconnects);
            UpdateProfileHelpText();
        }

        private void UpdateProfileHelpText()
        {
            if (profileHelpRuTextBox == null || profileHelpEnTextBox == null)
            {
                return;
            }

            UpdateHelpTextBox(profileHelpRuTextBox, EmbeddedHelpText.Russian);
            UpdateHelpTextBox(profileHelpEnTextBox, EmbeddedHelpText.English);
        }

        private static void UpdateHelpTextBox(RichTextBox textBox, string helpText)
        {
            if (String.Equals(textBox.Tag as string, helpText, StringComparison.Ordinal))
            {
                return;
            }

            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            Point scrollPosition = GetRichTextScrollPosition(textBox);
            SetControlRedraw(textBox, false);
            try
            {
                MarkdownHelpRenderer.Render(textBox, helpText);
                textBox.Tag = helpText;
                RestoreSelection(textBox, selectionStart, selectionLength);
                SetRichTextScrollPosition(textBox, scrollPosition);
            }
            finally
            {
                SetControlRedraw(textBox, true);
                textBox.Invalidate();
            }
        }

        private static void RestoreSelection(RichTextBox textBox, int selectionStart, int selectionLength)
        {
            int safeStart = Math.Min(selectionStart, textBox.TextLength);
            int safeLength = Math.Min(selectionLength, textBox.TextLength - safeStart);
            textBox.Select(safeStart, safeLength);
        }

        private static Point GetRichTextScrollPosition(RichTextBox textBox)
        {
            Point position = new Point();
            if (textBox.IsHandleCreated)
            {
                SendMessage(textBox.Handle, EmGetScrollPos, IntPtr.Zero, ref position);
            }

            return position;
        }

        private static void SetRichTextScrollPosition(RichTextBox textBox, Point position)
        {
            if (textBox.IsHandleCreated)
            {
                SendMessage(textBox.Handle, EmSetScrollPos, IntPtr.Zero, ref position);
            }
        }

        private static void SetControlRedraw(Control control, bool enabled)
        {
            if (control.IsHandleCreated)
            {
                SendMessage(control.Handle, WmSetRedraw, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static HelpTextSections LoadEmbeddedHelpText()
        {
            return new HelpTextSections(
                ReadEmbeddedText(HelpResourceRu, "# PlcsimGateway - справка" + Environment.NewLine + Environment.NewLine + "Справка недоступна."),
                ReadEmbeddedText(HelpResourceEn, "# PlcsimGateway - User Guide" + Environment.NewLine + Environment.NewLine + "Help is unavailable."));
        }

        private static string ReadEmbeddedText(string resourceName, string fallback)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return fallback;
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null;
            }
        }

        private string GetHealthText(GatewayStatus status, GatewayRuntimeStatus runtimeStatus)
        {
            if (!status.ListenerPid.HasValue)
            {
                return "STOPPED";
            }

            if (runtimeStatus == null)
            {
                return status.ClientSessions > 0 ? "CLIENT CONNECTED / NO SNAPSHOT" : "LISTENING / NO CLIENT";
            }

            int activeCount = runtimeStatus.activeSessions == null ? 0 : runtimeStatus.activeSessions.Count;
            if (activeCount == 0)
            {
                int disconnectCount = runtimeStatus.lastDisconnects == null ? 0 : runtimeStatus.lastDisconnects.Count;
                return disconnectCount > 0 ? "NO CLIENT / LAST WINCC DISCONNECT" : "LISTENING / NO CLIENT";
            }

            foreach (GatewayRuntimeSession session in runtimeStatus.activeSessions)
            {
                if (session.plcsimPdus > 0)
                {
                    return "CLIENT CONNECTED / PLCSIM LINK OK";
                }
            }

            return "CLIENT CONNECTED / PLCSIM WAIT";
        }

        private string GetSummaryText(GatewayRuntimeStatus runtimeStatus, string logTail)
        {
            if (runtimeStatus == null)
            {
                return FindLastSummary(logTail);
            }

            int activeCount = runtimeStatus.activeSessions == null ? 0 : runtimeStatus.activeSessions.Count;
            return "activeSessions=" + activeCount
                + " totalClientPdus=" + runtimeStatus.totalClientPdus
                + " totalClientBytes=" + runtimeStatus.totalClientBytes
                + " totalPlcsimPdus=" + runtimeStatus.totalPlcsimPdus
                + " totalPlcsimBytes=" + runtimeStatus.totalPlcsimBytes;
        }

        private void BindSessions(DataGridView grid, IList<GatewayRuntimeSession> sessions)
        {
            List<SessionRow> rows = new List<SessionRow>();
            if (sessions != null)
            {
                foreach (GatewayRuntimeSession session in sessions)
                {
                    rows.Add(new SessionRow(session));
                }
            }

            grid.DataSource = rows;
        }

        private string FindLastSummary(string logText)
        {
            if (String.IsNullOrWhiteSpace(logText))
            {
                return "-";
            }

            string[] lines = logText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                int summaryIndex = lines[i].IndexOf("SUMMARY", StringComparison.Ordinal);
                if (summaryIndex >= 0)
                {
                    return lines[i].Substring(summaryIndex);
                }
            }

            return "-";
        }

        private string GetSelectedProfileName()
        {
            GatewayProfile profile = GetSelectedProfile();
            if (profile == null)
            {
                throw new InvalidOperationException("No gateway profile selected.");
            }

            return profile.name;
        }

        private GatewayProfile GetSelectedProfile()
        {
            return profileComboBox.SelectedItem as GatewayProfile;
        }

        private void SetBusy(bool busy)
        {
            refreshButton.Enabled = !busy;
            saveProfileButton.Enabled = !busy;
            startButton.Enabled = !busy;
            stopButton.Enabled = !busy;
            restartButton.Enabled = !busy;
            profileComboBox.Enabled = !busy;
            listenAddressTextBox.Enabled = !busy;
            plcsimAddressTextBox.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void WriteOutput(string message, PowerShellResult result)
        {
            outputTextBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            if (result != null && !String.IsNullOrWhiteSpace(result.StandardOutput))
            {
                outputTextBox.AppendText(result.StandardOutput.Trim() + Environment.NewLine);
            }
        }

        private void WriteError(string message)
        {
            outputTextBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " ERROR " + message + Environment.NewLine);
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    && File.Exists(Path.Combine(directory.FullName, "config", "gateway-profiles.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            string fallback = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            if (File.Exists(Path.Combine(fallback, "config", "gateway-profiles.json")))
            {
                return fallback;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wParam,
            ref Point lParam);

        private sealed class StatusViewData
        {
            public GatewayStatus Status;
            public GatewayRuntimeStatus RuntimeStatus;
            public string LogTail;
        }

        private sealed class HelpTextSections
        {
            public HelpTextSections(string russian, string english)
            {
                Russian = russian;
                English = english;
            }

            public string Russian { get; private set; }
            public string English { get; private set; }
        }

        private sealed class SessionRow
        {
            public SessionRow(GatewayRuntimeSession session)
            {
                SessionId = session.sessionId.ToString();
                Remote = session.remoteEndPoint ?? "-";
                Duration = FormatDuration(session.durationMs);
                Protocol = session.protocol ?? "-";
                ClientPdus = session.clientPdus.ToString();
                PlcsimPdus = session.plcsimPdus.ToString();
                ClientBytes = session.clientBytes.ToString();
                PlcsimBytes = session.plcsimBytes.ToString();
                LastEvent = session.lastEvent ?? "-";
                LastEventAt = session.lastEventAt ?? "-";
            }

            public string SessionId { get; private set; }
            public string Remote { get; private set; }
            public string Duration { get; private set; }
            public string Protocol { get; private set; }
            public string ClientPdus { get; private set; }
            public string PlcsimPdus { get; private set; }
            public string ClientBytes { get; private set; }
            public string PlcsimBytes { get; private set; }
            public string LastEvent { get; private set; }
            public string LastEventAt { get; private set; }

            private static string FormatDuration(long durationMs)
            {
                if (durationMs < 0)
                {
                    durationMs = 0;
                }

                TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);
                if (duration.TotalHours >= 1)
                {
                    return ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00");
                }

                return duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00");
            }
        }
    }
}
