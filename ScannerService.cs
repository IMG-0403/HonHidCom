using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HonHidVerifier
{
    internal sealed class ScannerState
    {
        public bool Connected;
        public string Status;
        public string DeviceName;
        public string DeviceDetails;
        public string SerialNumber;
        public string ConnectionType;
    }

    internal sealed class CommandResult
    {
        public readonly bool Success;
        public readonly string Response;
        public readonly ScannerState State;

        public CommandResult(bool success, string response, ScannerState state)
        {
            Success = success;
            Response = response;
            State = state;
        }
    }

    internal sealed class ScannerService : IDisposable
    {
        private const string SettingsCommand =
            "TERMID?;PREBK2?;SUFBK2?;DFMBK3?;PLGFOE?;PLGDCE?;REVINF.";

        private readonly object _sync = new object();
        private List<HidDeviceInfo> _deviceList = new List<HidDeviceInfo>();
        private HidDeviceInfo _selectedDevice;
        private HidDeviceConnection _hidConnection;
        private SerialPort _serialConnection;
        private readonly StringBuilder _hidResponse = new StringBuilder();
        private readonly AutoResetEvent _hidDataReceived = new AutoResetEvent(false);
        private bool _waitingForHidResponse;

        private string _status = "未接続";
        private string _deviceName = "";
        private string _deviceDetails = "バーコードリーダーを接続して「再検出」をクリックしてください。";
        private string _serialNumber = "未接続";
        private string _connectionType = "";

        public ScannerState DetectAndConnect()
        {
            lock (_sync)
            {
                DisconnectCore();
                _status = "デバイス検出中";
                _serialNumber = "未接続";

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    _deviceList = HidEnumerator.Enumerate()
                        .OrderByDescending(device => device.IsHoneywellHidPosInterface)
                        .ThenByDescending(device => device.IsLikelyScanner)
                        .ThenByDescending(device => device.SupportsCommandOutput)
                        .ThenBy(device => device.Product)
                        .ToList();

                    HidDeviceInfo hid = _deviceList.FirstOrDefault(device =>
                        device.IsHoneywellCommandInterface);
                    if (hid != null)
                    {
                        ConnectHidCore(hid);
                        Thread.Sleep(3000);
                        _status = "接続中";
                        return GetStateCore();
                    }

                    string[] ports = HoneywellComPortEnumerator.GetPorts();
                    if (ports.Length > 0)
                    {
                        ConnectSerialCore(ports[0]);
                        return GetStateCore();
                    }

                    _status = "デバイス再接続待機中";
                    Thread.Sleep(500);
                }

                _status = "デバイスが見つかりません";
                _deviceName = "";
                _connectionType = "";
                _deviceDetails = "バーコードリーダーを接続して「再検出」をクリックしてください。";
                _serialNumber = "未接続";
                return GetStateCore();
            }
        }

        public ScannerState GetState()
        {
            lock (_sync)
                return GetStateCore();
        }

        public CommandResult SendSettingsCommand()
        {
            return SendCommand(SettingsCommand);
        }

        public CommandResult SendCommand(string command)
        {
            lock (_sync)
            {
                if (!IsConnectedCore)
                    return new CommandResult(false, "バーコードリーダーへ接続されていません。", GetStateCore());

                command = NormalizeCommand(command);
                if (command.Length == 0)
                    return new CommandResult(false, "送信するコマンドを入力してください。", GetStateCore());

                try
                {
                    string response = _serialConnection != null && _serialConnection.IsOpen
                        ? SendSerialCommandCore(command, 8000)
                        : SendHidCommandWithRetryCore(command);

                    if (command.StartsWith("DEFALT", StringComparison.OrdinalIgnoreCase))
                    {
                        DisconnectCore();
                        Thread.Sleep(3000);
                        DetectAndConnect();
                        return new CommandResult(true,
                            "初期化コマンドを送信しました。デバイスの再接続が完了しました。",
                            GetStateCore());
                    }

                    if (string.IsNullOrWhiteSpace(response))
                        response = "コマンドの応答がありませんでした。";
                    else
                        UpdateSerialFromResponseCore(response);

                    return new CommandResult(true, response, GetStateCore());
                }
                catch (Exception ex)
                {
                    return new CommandResult(false, "送信エラー: " + ex.Message, GetStateCore());
                }
            }
        }

        private string SendHidCommandWithRetryCore(string command)
        {
            string response = SendHidCommandCore(command, 8000);
            if (!string.IsNullOrWhiteSpace(response))
                return response;

            HidDeviceInfo selected = _selectedDevice;
            if (selected == null)
                return response;

            DisconnectCore();
            Thread.Sleep(1500);
            _deviceList = HidEnumerator.Enumerate();
            HidDeviceInfo refreshed = _deviceList.FirstOrDefault(device =>
                device.IsHoneywellHidPosInterface &&
                device.VendorId == selected.VendorId &&
                device.ProductId == selected.ProductId);
            if (refreshed == null)
                return response;

            ConnectHidCore(refreshed);
            Thread.Sleep(3000);
            return SendHidCommandCore(command, 8000);
        }

        private string SendHidCommandCore(string command, int timeoutMs)
        {
            byte[] menuCommand = BuildMenuCommand(command);
            if (menuCommand.Length > 62)
                throw new InvalidOperationException(
                    "コマンドが長すぎます。SYN M CRを含めて62バイト以内にしてください。");

            byte[] payload = new byte[menuCommand.Length + 1];
            payload[0] = (byte)menuCommand.Length;
            Buffer.BlockCopy(menuCommand, 0, payload, 1, menuCommand.Length);

            lock (_hidResponse)
                _hidResponse.Length = 0;
            _waitingForHidResponse = true;
            _hidConnection.Send(payload, CommandTransport.OutputReport, 0xFD);

            DateTime limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool received = false;
            while (DateTime.UtcNow < limit)
            {
                int wait = received ? 700 : 250;
                if (_hidDataReceived.WaitOne(wait))
                {
                    received = true;
                    continue;
                }
                if (received)
                    break;
            }
            _waitingForHidResponse = false;
            lock (_hidResponse)
                return _hidResponse.ToString();
        }

        private string SendSerialCommandCore(string command, int timeoutMs)
        {
            byte[] menuCommand = BuildMenuCommand(command);
            SerialPort port = _serialConnection;
            port.DiscardInBuffer();
            port.Write(menuCommand, 0, menuCommand.Length);

            var response = new StringBuilder();
            DateTime limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            DateTime? lastData = null;
            while (DateTime.UtcNow < limit)
            {
                int count = port.BytesToRead;
                if (count > 0)
                {
                    byte[] data = new byte[count];
                    int read = port.Read(data, 0, data.Length);
                    response.Append(Encoding.ASCII.GetString(data, 0, read).Replace("\0", ""));
                    lastData = DateTime.UtcNow;
                }
                else
                {
                    if (lastData.HasValue &&
                        (DateTime.UtcNow - lastData.Value).TotalMilliseconds >= 700)
                        break;
                    Thread.Sleep(50);
                }
            }
            return response.ToString();
        }

        private void ConnectHidCore(HidDeviceInfo selected)
        {
            _selectedDevice = selected;
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

            _hidConnection = new HidDeviceConnection(commandInterface);
            _hidConnection.ReportReceived += OnHidReportReceived;
            _hidConnection.ConnectionError += delegate { };
            _hidConnection.Open();

            _connectionType = "HID POS";
            _deviceName = selected.ToString();
            _deviceDetails = FormatHidDetails(selected, commandInterface);
            _serialNumber = EmptyAsUnknown(selected.SerialNumber);
            _status = "接続中";
        }

        private void ConnectSerialCore(string portName)
        {
            _serialConnection = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
            _serialConnection.Handshake = Handshake.None;
            _serialConnection.DtrEnable = true;
            _serialConnection.RtsEnable = true;
            _serialConnection.ReadTimeout = 1000;
            _serialConnection.WriteTimeout = 3000;
            _serialConnection.Open();

            _connectionType = "USB-COM";
            _deviceName = "Honeywell USB-COM (" + portName + ")";
            _deviceDetails = "Honeywell USB-COM: " + portName +
                "\r\n115200 bps / 8 data bits / parity none / 1 stop bit";
            _status = "USB-COM接続中";
            _serialNumber = "取得中...";

            string info = SendSerialCommandCore("P_INFO.", 5000);
            Match match = Regex.Match(info,
                @"(?im)^\s*hw-sn\s*:\s*([A-Z0-9][A-Z0-9._/-]{3,})");
            _serialNumber = match.Success ? match.Groups[1].Value : "(取得できませんでした)";
        }

        private void OnHidReportReceived(byte[] report)
        {
            if (!_waitingForHidResponse)
                return;

            string text = HidReportParser.ExtractText(report);
            lock (_hidResponse)
            {
                if (string.IsNullOrEmpty(text))
                    _hidResponse.AppendLine(ToHex(report));
                else
                    _hidResponse.Append(text);
            }
            _hidDataReceived.Set();
        }

        private void UpdateSerialFromResponseCore(string response)
        {
            Match match = Regex.Match(response,
                @"(?i)(?:SERIAL(?:\s*(?:NO|NUMBER))?|S/N|SN)\s*[:=,\s]\s*([A-Z0-9][A-Z0-9._/-]{3,})");
            if (match.Success)
                _serialNumber = match.Groups[1].Value;
        }

        private bool IsConnectedCore
        {
            get
            {
                return _hidConnection != null ||
                    (_serialConnection != null && _serialConnection.IsOpen);
            }
        }

        private ScannerState GetStateCore()
        {
            return new ScannerState
            {
                Connected = IsConnectedCore,
                Status = _status,
                DeviceName = _deviceName,
                DeviceDetails = _deviceDetails,
                SerialNumber = _serialNumber,
                ConnectionType = _connectionType
            };
        }

        private void DisconnectCore()
        {
            _waitingForHidResponse = false;
            if (_hidConnection != null)
            {
                _hidConnection.ReportReceived -= OnHidReportReceived;
                _hidConnection.Dispose();
                _hidConnection = null;
            }
            if (_serialConnection != null)
            {
                try
                {
                    if (_serialConnection.IsOpen)
                        _serialConnection.Close();
                    _serialConnection.Dispose();
                }
                catch { }
                _serialConnection = null;
            }
            _selectedDevice = null;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                DisconnectCore();
                _hidDataReceived.Close();
            }
        }

        private static byte[] BuildMenuCommand(string command)
        {
            byte[] commandBody = Encoding.ASCII.GetBytes(command);
            byte[] menuCommand = new byte[commandBody.Length + 3];
            menuCommand[0] = 22;
            menuCommand[1] = 77;
            menuCommand[2] = 13;
            Buffer.BlockCopy(commandBody, 0, menuCommand, 3, commandBody.Length);
            return menuCommand;
        }

        private static string NormalizeCommand(string command)
        {
            command = (command ?? "").Trim();
            if (command.Length > 0 && !command.EndsWith(".", StringComparison.Ordinal))
                command += ".";
            return command;
        }

        private static string FormatHidDetails(HidDeviceInfo selected, HidDeviceInfo commandInterface)
        {
            string commandNote = ReferenceEquals(selected, commandInterface)
                ? ""
                : "\r\n通信先: REMインターフェース";
            return string.Format(
                "製品名: {0}\r\nメーカー: {1}\r\nVID: {2:X4}　PID: {3:X4}　UsagePage: {4:X4}　Usage: {5:X4}\r\nInput: {6} byte　Output: {7} byte　Feature: {8} byte{9}",
                EmptyAsUnknown(selected.Product), EmptyAsUnknown(selected.Manufacturer),
                selected.VendorId, selected.ProductId, selected.UsagePage, selected.Usage,
                selected.InputReportLength, selected.OutputReportLength,
                selected.FeatureReportLength, commandNote);
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(不明)" : value;
        }

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }
    }
}
