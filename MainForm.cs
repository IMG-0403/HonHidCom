using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HonHidVerifier
{
    internal sealed class MainForm : Form
    {
        private readonly ComboBox _devices = new ComboBox();
        private readonly Button _refreshButton = new Button();
        private readonly Button _connectButton = new Button();
        private readonly Button _deviceInfoButton = new Button();
        private readonly TextBox _manualCommand = new TextBox();
        private readonly Button _manualSendButton = new Button();
        private readonly Button _clearResponseButton = new Button();
        private readonly Button _copyResponseButton = new Button();
        private readonly Label _connectionStatus = new Label();
        private readonly Label _deviceDetails = new Label();
        private readonly Label _serialNumber = new Label();
        private readonly TextBox _response = new TextBox();
        private readonly System.Windows.Forms.Timer _responseTimer =
            new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _serialInfoTimer =
            new System.Windows.Forms.Timer();

        private List<HidDeviceInfo> _deviceList = new List<HidDeviceInfo>();
        private HidDeviceConnection _connection;
        private SerialPort _serialConnection;
        private bool _waitingForRevision;
        private bool _receivedRevision;
        private bool _detecting;
        private bool _recoveringCommand;
        private bool _waitingForSerialInfo;
        private int _automaticRetryCount;
        private string _pendingCommand;
        private string _pendingDisplayName;
        private readonly StringBuilder _revisionResponse = new StringBuilder();
        private readonly StringBuilder _serialInfoResponse = new StringBuilder();

        public MainForm()
        {
            Text = "Honeywell バーコードリーダー デバイス情報";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(780, 580);
            Size = new Size(920, 700);
            Font = new Font("Yu Gothic UI", 9f);

            BuildUi();
            FormClosing += OnFormClosing;
            Shown += async delegate { await DetectAndConnectAsync(); };

            _responseTimer.Tick += OnResponseTimerTick;
            _serialInfoTimer.Tick += OnSerialInfoTimerTick;
        }

        private void OnResponseTimerTick(object sender, EventArgs e)
        {
            _responseTimer.Stop();
            if (!_waitingForRevision)
                return;

            if (!_receivedRevision && _connection != null &&
                _automaticRetryCount == 0 && !_recoveringCommand)
            {
                _automaticRetryCount++;
                RetryHidCommandAfterReconnectAsync();
                return;
            }

            if (!_receivedRevision)
                _response.Text = "コマンドの応答がありませんでした。";
            else
                UpdateSerialFromRevisionResponse();

            _waitingForRevision = false;
            _deviceInfoButton.Enabled = IsConnected;
            _manualSendButton.Enabled = IsConnected;
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 11
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = "Honeywell バーコードリーダー デバイス情報",
                Font = new Font("Yu Gothic UI", 19f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 14)
            };

            var deviceRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0)
            };
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var deviceLabel = new Label
            {
                Text = "接続デバイス",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 9, 0)
            };
            _devices.Dock = DockStyle.Fill;
            _devices.DropDownStyle = ComboBoxStyle.DropDownList;
            _devices.SelectedIndexChanged += delegate
            {
                UpdateDeviceDetails();
                if (_connection != null)
                    Disconnect();
            };
            _refreshButton.Text = "再検出";
            _refreshButton.AutoSize = true;
            _refreshButton.Margin = new Padding(8, 0, 0, 0);
            _refreshButton.Click += async delegate { await DetectAndConnectAsync(); };
            _connectButton.Text = "接続";
            _connectButton.AutoSize = true;
            _connectButton.Margin = new Padding(8, 0, 0, 0);
            _connectButton.Click += delegate
            {
                if (!IsConnected)
                {
                    string comPort = _devices.SelectedItem as string;
                    if (!string.IsNullOrEmpty(comPort))
                        ConnectSerialPort(comPort, true);
                    else
                        ConnectSelectedDevice(true);
                }
                else
                    Disconnect();
            };
            deviceRow.Controls.Add(deviceLabel, 0, 0);
            deviceRow.Controls.Add(_devices, 1, 0);
            deviceRow.Controls.Add(_refreshButton, 2, 0);
            deviceRow.Controls.Add(_connectButton, 3, 0);

            _connectionStatus.Text = "● 検出中";
            _connectionStatus.ForeColor = Color.DimGray;
            _connectionStatus.AutoSize = true;
            _connectionStatus.Margin = new Padding(0, 8, 0, 5);

            _deviceDetails.Text = "デバイス情報を取得しています。";
            _deviceDetails.AutoSize = true;
            _deviceDetails.MaximumSize = new Size(850, 0);
            _deviceDetails.ForeColor = Color.DimGray;
            _deviceDetails.Margin = new Padding(0, 0, 0, 12);

            var serialLabel = new Label
            {
                Text = "シリアルNo",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
            _serialNumber.Text = "未接続";
            _serialNumber.Font = new Font("Segoe UI", 28f, FontStyle.Bold);
            _serialNumber.ForeColor = Color.FromArgb(20, 74, 130);
            _serialNumber.AutoSize = true;
            _serialNumber.Margin = new Padding(0, 0, 0, 12);

            _deviceInfoButton.Text = "デバイス設定出力";
            _deviceInfoButton.AutoSize = true;
            _deviceInfoButton.Enabled = false;
            _deviceInfoButton.BackColor = Color.FromArgb(25, 118, 210);
            _deviceInfoButton.ForeColor = Color.White;
            _deviceInfoButton.FlatStyle = FlatStyle.Flat;
            _deviceInfoButton.Padding = new Padding(12, 4, 12, 4);
            _deviceInfoButton.Margin = new Padding(0, 0, 0, 12);
            _deviceInfoButton.Click += delegate { SendRevisionInformation(); };

            var manualCommandRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 8, 0, 4)
            };
            manualCommandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            manualCommandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _manualCommand.Dock = DockStyle.Fill;
            _manualCommand.Font = new Font("Consolas", 10f);
            _manualCommand.Text = "";
            _manualSendButton.Text = "コマンド送信";
            _manualSendButton.AutoSize = false;
            _manualSendButton.Size = new Size(110, 30);
            _manualSendButton.Enabled = false;
            _manualSendButton.Margin = new Padding(8, 0, 0, 0);
            _manualSendButton.Click += delegate { SendManualCommand(); };
            manualCommandRow.Controls.Add(_manualCommand, 0, 0);
            manualCommandRow.Controls.Add(_manualSendButton, 1, 0);

            var responseHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 7)
            };
            responseHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            responseHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var responseLabel = new Label
            {
                Text = "応答結果",
                Font = new Font("Yu Gothic UI", 12f, FontStyle.Bold),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 0, 0)
            };
            ConfigureOutputBox(_response);
            _response.Font = new Font("Consolas", 11f);

            var responseButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(8, 0, 0, 0)
            };
            _clearResponseButton.Text = "クリア";
            _clearResponseButton.AutoSize = false;
            _clearResponseButton.Size = new Size(82, 30);
            _clearResponseButton.Margin = new Padding(0);
            _clearResponseButton.Click += delegate
            {
                _response.Clear();
                _revisionResponse.Length = 0;
            };
            _copyResponseButton.Text = "コピー";
            _copyResponseButton.AutoSize = false;
            _copyResponseButton.Size = new Size(82, 30);
            _copyResponseButton.Margin = new Padding(8, 0, 0, 0);
            _copyResponseButton.Click += delegate
            {
                if (!string.IsNullOrEmpty(_response.Text))
                    Clipboard.SetText(_response.Text);
            };
            responseButtons.Controls.Add(_clearResponseButton);
            responseButtons.Controls.Add(_copyResponseButton);
            responseHeader.Controls.Add(responseLabel, 0, 0);
            responseHeader.Controls.Add(responseButtons, 1, 0);

            var note = new Label
            {
                Text = "送信コマンド: TERMID?;PREBK2?;SUFBK2?;DFMBK3?;PLGFOE?;PLGDCE?;REVINF.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 8, 0, 0)
            };

            root.Controls.Add(title, 0, 0);
            root.Controls.Add(deviceRow, 0, 1);
            root.Controls.Add(manualCommandRow, 0, 2);
            root.Controls.Add(_connectionStatus, 0, 3);
            root.Controls.Add(_deviceDetails, 0, 4);
            root.Controls.Add(serialLabel, 0, 5);
            root.Controls.Add(_serialNumber, 0, 6);
            root.Controls.Add(_deviceInfoButton, 0, 7);
            root.Controls.Add(responseHeader, 0, 8);
            root.Controls.Add(_response, 0, 9);
            root.Controls.Add(note, 0, 10);
            Controls.Add(root);
        }

        private static void ConfigureOutputBox(TextBox box)
        {
            box.Dock = DockStyle.Fill;
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.BackColor = Color.White;
            box.Font = new Font("Consolas", 10f);
        }

        private async Task DetectAndConnectAsync()
        {
            if (_detecting)
                return;
            _detecting = true;
            Disconnect();
            Cursor = Cursors.WaitCursor;
            _refreshButton.Enabled = false;
            _connectionStatus.Text = "● デバイス検出中";
            _connectionStatus.ForeColor = Color.DimGray;
            try
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    _deviceList = HidEnumerator.Enumerate()
                        .OrderByDescending(device => device.IsHoneywellHidPosInterface)
                        .ThenByDescending(device => device.IsLikelyScanner)
                        .ThenByDescending(device => device.SupportsCommandOutput)
                        .ThenBy(device => device.Product)
                        .ToList();

                    int honeywellIndex = _deviceList.FindIndex(device =>
                        device.IsHoneywellCommandInterface);
                    if (honeywellIndex >= 0)
                    {
                        _devices.Items.Clear();
                        foreach (HidDeviceInfo device in _deviceList)
                            _devices.Items.Add(device);
                        _devices.SelectedIndex = honeywellIndex;
                        UpdateDeviceDetails();

                        // DEFALT後は列挙直後でもファームウェア初期化中の場合がある。
                        await Task.Delay(500);
                        ConnectSelectedDevice(false);
                        if (_connection != null)
                        {
                            _connectionStatus.Text = "● デバイス初期化待機中";
                            _connectionStatus.ForeColor = Color.DarkOrange;
                            _deviceInfoButton.Enabled = false;
                            _manualSendButton.Enabled = false;
                        await Task.Delay(3000);
                            if (_connection != null)
                            {
                                _connectionStatus.Text = "● 接続中";
                                _connectionStatus.ForeColor = Color.ForestGreen;
                                _deviceInfoButton.Enabled = true;
                                _manualSendButton.Enabled = true;
                            }
                            return;
                        }
                    }

                    string[] honeywellComPorts = HoneywellComPortEnumerator.GetPorts();
                    if (honeywellComPorts.Length > 0)
                    {
                        _devices.Items.Clear();
                        foreach (string port in honeywellComPorts)
                            _devices.Items.Add(port);
                        _devices.SelectedIndex = 0;
                        _deviceDetails.Text = "Honeywell USB-COM: " +
                            string.Join(", ", honeywellComPorts) +
                            "\r\n115200 bps / 8 data bits / parity none / 1 stop bit";
                        HidDeviceInfo honeywell = _deviceList.FirstOrDefault(
                            device => device.VendorId == 0x0C2E);
                        _serialNumber.Text = honeywell == null
                            ? "未接続" : EmptyAsUnknown(honeywell.SerialNumber);
                        ConnectSerialPort(honeywellComPorts[0], false);
                        return;
                    }

                    _connectionStatus.Text = "● デバイス再接続待機中";
                    _connectionStatus.ForeColor = Color.DarkOrange;
                    await Task.Delay(500);
                }

                _devices.Items.Clear();
                _connectionStatus.Text = "● HIDデバイスが見つかりません";
                _connectionStatus.ForeColor = Color.Firebrick;
                _deviceDetails.Text =
                    "バーコードリーダーを接続して「再検出」をクリックしてください。";
            }
            catch (Exception ex)
            {
                _connectionStatus.Text = "● デバイス検出エラー";
                _connectionStatus.ForeColor = Color.Firebrick;
                _deviceDetails.Text = ex.Message;
            }
            finally
            {
                _detecting = false;
                _refreshButton.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private bool IsConnected
        {
            get
            {
                return _connection != null ||
                    (_serialConnection != null && _serialConnection.IsOpen);
            }
        }

        private void ConnectSerialPort(string portName, bool showError)
        {
            try
            {
                Disconnect();
                _serialConnection = new SerialPort(portName, 115200,
                    Parity.None, 8, StopBits.One);
                _serialConnection.Handshake = Handshake.None;
                _serialConnection.DtrEnable = true;
                _serialConnection.RtsEnable = true;
                _serialConnection.ReadTimeout = 1000;
                _serialConnection.WriteTimeout = 3000;
                _serialConnection.DataReceived += OnSerialDataReceived;
                _serialConnection.Open();

                _connectionStatus.Text = "● USB-COM接続中 (" + portName + ")";
                _connectionStatus.ForeColor = Color.ForestGreen;
                _connectButton.Text = "切断";
                _connectButton.Enabled = true;
                _deviceInfoButton.Enabled = false;
                _manualSendButton.Enabled = false;
                _serialNumber.Text = "取得中...";
                BeginSerialInfoQueryAsync(_serialConnection);
            }
            catch (Exception ex)
            {
                Disconnect();
                _connectionStatus.Text = "● USB-COM接続エラー";
                _connectionStatus.ForeColor = Color.Firebrick;
                _deviceDetails.Text = ex.Message;
                if (showError)
                    MessageBox.Show(this, ex.Message, "USB-COM接続エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort port = _serialConnection;
                if (port == null || !port.IsOpen)
                    return;
                int count = port.BytesToRead;
                if (count <= 0)
                    return;
                byte[] data = new byte[count];
                int read = port.Read(data, 0, data.Length);
                if (read != data.Length)
                    Array.Resize(ref data, read);
                string text = Encoding.ASCII.GetString(data)
                    .Replace("\0", "");
                BeginInvoke(new Action(() =>
                {
                    if (_waitingForSerialInfo)
                        HandleSerialInfoText(text);
                    else
                        HandleReceivedText(text, data);
                }));
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() =>
                {
                    _connectionStatus.Text = "● USB-COM受信エラー";
                    _connectionStatus.ForeColor = Color.Firebrick;
                    _deviceDetails.Text = ex.Message;
                }));
            }
        }

        private async void BeginSerialInfoQueryAsync(SerialPort expectedPort)
        {
            await Task.Delay(300);
            SerialPort port = _serialConnection;
            if (port == null || !ReferenceEquals(port, expectedPort) || !port.IsOpen)
                return;

            try
            {
                byte[] command = Encoding.ASCII.GetBytes(
                    "\x16\x4D\x0DP_INFO.");
                _serialInfoResponse.Length = 0;
                _waitingForSerialInfo = true;
                _serialInfoTimer.Stop();
                _serialInfoTimer.Interval = 5000;
                _serialInfoTimer.Start();
                port.DiscardInBuffer();
                port.Write(command, 0, command.Length);
            }
            catch
            {
                FinishSerialInfoQuery();
            }
        }

        private void HandleSerialInfoText(string text)
        {
            if (!_waitingForSerialInfo)
                return;

            _serialInfoResponse.Append(text);
            _serialInfoTimer.Stop();
            _serialInfoTimer.Interval = 700;
            _serialInfoTimer.Start();
        }

        private void OnSerialInfoTimerTick(object sender, EventArgs e)
        {
            _serialInfoTimer.Stop();
            FinishSerialInfoQuery();
        }

        private void FinishSerialInfoQuery()
        {
            _serialInfoTimer.Stop();
            _waitingForSerialInfo = false;

            Match match = Regex.Match(_serialInfoResponse.ToString(),
                @"(?im)^\s*hw-sn\s*:\s*([A-Z0-9][A-Z0-9._/-]{3,})");
            if (match.Success)
                _serialNumber.Text = match.Groups[1].Value;
            else if (_serialConnection != null && _serialConnection.IsOpen)
                _serialNumber.Text = "(取得できませんでした)";

            _deviceInfoButton.Enabled = IsConnected;
            _manualSendButton.Enabled = IsConnected;
        }

        private void ConnectSelectedDevice(bool showError)
        {
            HidDeviceInfo selected = _devices.SelectedItem as HidDeviceInfo;
            if (selected == null)
                return;

            try
            {
                Disconnect();
                // Honeywell複合USBデバイスでは、画面上の接続方式が
                // HID POSでもメニューコマンドの送受信先はREMとなる。
                // ShowHidComのinterface redirectと同じ経路を内部で選ぶ。
                HidDeviceInfo commandInterface = selected;
                if (selected.IsHoneywellHidPosInterface)
                {
                    HidDeviceInfo remote = _deviceList.FirstOrDefault(device =>
                        device.IsHoneywellRemoteInterface &&
                        device.VendorId == selected.VendorId &&
                        device.ProductId == selected.ProductId &&
                        device.SupportsCommandOutput);
                    if (remote != null)
                        commandInterface = remote;
                }

                _connection = new HidDeviceConnection(commandInterface);
                _connection.ReportReceived += OnReportReceived;
                _connection.ConnectionError += message => BeginInvoke(new Action(() =>
                {
                    _connectionStatus.Text = "● 受信エラー";
                    _connectionStatus.ForeColor = Color.Firebrick;
                    Disconnect();
                }));
                _connection.Open();
                _connectionStatus.Text = "● 接続中";
                _connectionStatus.ForeColor = Color.ForestGreen;
                _connectButton.Text = "切断";
                _deviceInfoButton.Enabled = true;
                _manualSendButton.Enabled = true;
                _serialNumber.Text = EmptyAsUnknown(selected.SerialNumber);
            }
            catch (Exception ex)
            {
                Disconnect();
                _connectionStatus.Text = "● 接続できません";
                _connectionStatus.ForeColor = Color.Firebrick;
                if (showError)
                    MessageBox.Show(this, ex.Message, "接続エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SendRevisionInformation()
        {
            const string commandText =
                "TERMID?;PREBK2?;SUFBK2?;DFMBK3?;PLGFOE?;PLGDCE?;REVINF.";
            SendMenuCommand(commandText, "デバイス設定コマンド");
        }

        private void SendManualCommand()
        {
            string command = (_manualCommand.Text ?? "").Trim();
            if (command.Length == 0)
            {
                MessageBox.Show(this, "送信するコマンドを入力してください。");
                return;
            }
            if (!command.EndsWith(".", StringComparison.Ordinal))
                command += ".";
            SendMenuCommand(command, "手動コマンド");
        }

        private void SendMenuCommand(string command, string displayName)
        {
            _automaticRetryCount = 0;
            SendMenuCommandCore(command, displayName);
        }

        private void SendMenuCommandCore(string command, string displayName)
        {
            if (!IsConnected)
            {
                MessageBox.Show(this, "バーコードリーダーへ接続されていません。");
                return;
            }

            try
            {
                byte[] commandBody = Encoding.ASCII.GetBytes(command);
                byte[] menuCommand = new byte[commandBody.Length + 3];
                menuCommand[0] = 22;
                menuCommand[1] = 77;
                menuCommand[2] = 13;
                Buffer.BlockCopy(commandBody, 0, menuCommand, 3, commandBody.Length);
                if (menuCommand.Length > 62)
                    throw new InvalidOperationException(
                        "コマンドが長すぎます。SYN M CRを含めて62バイト以内にしてください。");

                _response.Text = displayName + "を送信しました。応答待機中...";
                _pendingCommand = command;
                _pendingDisplayName = displayName;
                _revisionResponse.Length = 0;
                _waitingForRevision = true;
                _receivedRevision = false;
                _deviceInfoButton.Enabled = false;
                _manualSendButton.Enabled = false;
                _responseTimer.Stop();
                _responseTimer.Interval = 8000;
                _responseTimer.Start();
                if (_serialConnection != null && _serialConnection.IsOpen)
                {
                    _serialConnection.DiscardInBuffer();
                    _serialConnection.Write(menuCommand, 0, menuCommand.Length);
                }
                else
                {
                    byte[] payload = new byte[menuCommand.Length + 1];
                    payload[0] = (byte)menuCommand.Length;
                    Buffer.BlockCopy(menuCommand, 0, payload, 1, menuCommand.Length);
                    _connection.Send(payload, CommandTransport.OutputReport, 0xFD);
                }

                if (command.StartsWith("DEFALT", StringComparison.OrdinalIgnoreCase))
                {
                    _responseTimer.Stop();
                    _waitingForRevision = false;
                    _response.Text =
                        "初期化コマンドを送信しました。デバイスを再接続しています...";
                    BeginResetReconnect();
                }
            }
            catch (Exception ex)
            {
                _responseTimer.Stop();
                _waitingForRevision = false;
                if (_connection != null && _automaticRetryCount == 0 &&
                    !_recoveringCommand)
                {
                    _automaticRetryCount++;
                    _response.Text =
                        "HID POS送信をやり直しています: " + ex.Message;
                    RetryHidCommandAfterReconnectAsync();
                    return;
                }
                _deviceInfoButton.Enabled = IsConnected;
                _manualSendButton.Enabled = IsConnected;
                _response.Text = "送信エラー: " + ex.Message;
            }
        }

        private async void RetryHidCommandAfterReconnectAsync()
        {
            _recoveringCommand = true;
            string command = _pendingCommand;
            string displayName = _pendingDisplayName;
            _waitingForRevision = false;
            _response.Text =
                "HID POSの通信準備をやり直しています。コマンドを自動再送します...";
            _deviceInfoButton.Enabled = false;
            _manualSendButton.Enabled = false;

            try
            {
                Disconnect();
                await Task.Delay(1500);
                await DetectAndConnectAsync();
                if (_connection == null)
                    throw new InvalidOperationException(
                        "HID POSへ再接続できませんでした。");

                // USBインターフェース切替後は、列挙・オープン完了後も
                // スキャナのメニューコマンド処理が準備中の場合がある。
                await Task.Delay(3000);
                if (_connection == null)
                    throw new InvalidOperationException(
                        "HID POS接続が切断されました。");
                SendMenuCommandCore(command, displayName + "（自動再送）");
            }
            catch (Exception ex)
            {
                _waitingForRevision = false;
                _response.Text = "HID POS再接続エラー: " + ex.Message;
                _deviceInfoButton.Enabled = IsConnected;
                _manualSendButton.Enabled = IsConnected;
            }
            finally
            {
                _recoveringCommand = false;
            }
        }

        private async void BeginResetReconnect()
        {
            await Task.Delay(300);
            Disconnect();
            await Task.Delay(3000);
            await DetectAndConnectAsync();
            if (IsConnected)
                _response.Text = "デバイスの再接続が完了しました。";
        }

        private void OnReportReceived(byte[] report)
        {
            string text = HidReportParser.ExtractText(report);
            BeginInvoke(new Action(() => HandleReceivedText(text, report)));
        }

        private void HandleReceivedText(string text, byte[] rawData)
        {
            if (!_waitingForRevision)
                return;

            bool firstReport = !_receivedRevision;
            _receivedRevision = true;
            if (firstReport)
                _response.Clear();
            if (string.IsNullOrEmpty(text))
                _response.AppendText(ToHex(rawData) + Environment.NewLine);
            else
                _response.AppendText(text);
            if (!string.IsNullOrEmpty(text))
                _revisionResponse.Append(text);

            _responseTimer.Stop();
            _responseTimer.Interval = 700;
            _responseTimer.Start();
        }

        private void UpdateSerialFromRevisionResponse()
        {
            string response = _revisionResponse.ToString();
            if (string.IsNullOrWhiteSpace(response))
                return;

            Match match = Regex.Match(response,
                @"(?i)(?:SERIAL(?:\s*(?:NO|NUMBER))?|S/N|SN)\s*[:=,\s]\s*([A-Z0-9][A-Z0-9._/-]{3,})");
            if (match.Success)
                _serialNumber.Text = match.Groups[1].Value;
        }

        private void UpdateDeviceDetails()
        {
            HidDeviceInfo device = _devices.SelectedItem as HidDeviceInfo;
            if (device == null)
            {
                _deviceDetails.Text = "デバイスが選択されていません。";
                return;
            }

            _deviceDetails.Text = string.Format(
                "製品名: {0}\r\nメーカー: {1}\r\nVID: {2:X4}　PID: {3:X4}　UsagePage: {4:X4}　Usage: {5:X4}\r\nInput: {6} byte　Output: {7} byte　Feature: {8} byte",
                EmptyAsUnknown(device.Product), EmptyAsUnknown(device.Manufacturer),
                device.VendorId, device.ProductId, device.UsagePage, device.Usage,
                device.InputReportLength, device.OutputReportLength,
                device.FeatureReportLength);
        }

        private void Disconnect()
        {
            _responseTimer.Stop();
            _serialInfoTimer.Stop();
            _waitingForRevision = false;
            _waitingForSerialInfo = false;
            if (_connection != null)
            {
                _connection.Dispose();
                _connection = null;
            }
            if (_serialConnection != null)
            {
                try
                {
                    _serialConnection.DataReceived -= OnSerialDataReceived;
                    if (_serialConnection.IsOpen)
                        _serialConnection.Close();
                    _serialConnection.Dispose();
                }
                catch { }
                _serialConnection = null;
            }
            _connectButton.Text = "接続";
            _connectButton.Enabled = _devices.SelectedItem != null;
            _deviceInfoButton.Enabled = false;
            _manualSendButton.Enabled = false;
            _serialNumber.Text = "未接続";
        }

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(不明)" : value;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            Disconnect();
            _responseTimer.Dispose();
            _serialInfoTimer.Dispose();
        }

    }
}
