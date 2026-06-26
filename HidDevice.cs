using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HonHidVerifier
{
    internal enum CommandTransport
    {
        OutputReport,
        FeatureReport,
        None
    }

    internal sealed class HidDeviceInfo
    {
        public string DevicePath { get; set; }
        public string Product { get; set; }
        public string Manufacturer { get; set; }
        public string SerialNumber { get; set; }
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public ushort UsagePage { get; set; }
        public ushort Usage { get; set; }
        public short InputReportLength { get; set; }
        public short OutputReportLength { get; set; }
        public short FeatureReportLength { get; set; }
        public List<byte> OutputReportIds { get; set; }
        public List<string> OutputCapabilities { get; set; }
        public List<string> InputCapabilities { get; set; }

        public HidDeviceInfo()
        {
            OutputReportIds = new List<byte>();
            OutputCapabilities = new List<string>();
            InputCapabilities = new List<string>();
        }

        public bool IsLikelyScanner
        {
            get
            {
                string text = ((Manufacturer ?? "") + " " + (Product ?? "")).ToLowerInvariant();
                return VendorId == 0x0C2E || VendorId == 0x0536 ||
                    text.Contains("honeywell") || text.Contains("hand held") ||
                    text.Contains("scanner") || text.Contains("barcode") ||
                    UsagePage == 0x8C;
            }
        }

        public bool SupportsCommandOutput
        {
            // Keyboard LED用の2 byte Output Reportはコマンド送信用ではない。
            get { return OutputReportLength > 2 || FeatureReportLength > 2; }
        }

        public bool IsHoneywellRemoteInterface
        {
            get
            {
                return VendorId == 0x0C2E &&
                    (UsagePage == 0xFF8C ||
                     string.Equals(Product, "REM", StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsHoneywellCommandInterface
        {
            get
            {
                return SupportsCommandOutput &&
                    (IsHoneywellHidPosInterface || IsHoneywellRemoteInterface);
            }
        }

        public bool IsHoneywellHidPosInterface
        {
            get
            {
                return VendorId == 0x0C2E &&
                    UsagePage == 0x008C &&
                    Usage == 0x0002;
            }
        }

        public override string ToString()
        {
            string name = string.IsNullOrWhiteSpace(Product) ? "HID Device" : Product;
            return string.Format("{0}  [VID:{1:X4} PID:{2:X4} UP:{3:X2} U:{4:X2}]",
                name, VendorId, ProductId, UsagePage, Usage);
        }
    }

    internal sealed class HidDeviceConnection : IDisposable
    {
        private readonly HidDeviceInfo _info;
        private Microsoft.Win32.SafeHandles.SafeFileHandle _readHandle;
        private Microsoft.Win32.SafeHandles.SafeFileHandle _writeHandle;
        private Thread _readThread;
        private readonly ManualResetEvent _readReady = new ManualResetEvent(false);
        private volatile bool _reading;

        public event Action<byte[]> ReportReceived;
        public event Action<string> ConnectionError;

        public HidDeviceConnection(HidDeviceInfo info)
        {
            _info = info;
        }

        public void Open()
        {
            // HID POSは同一のオーバーラップハンドルで入出力する。
            _readHandle = NativeMethods.CreateFile(_info.DevicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (_readHandle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HID機器を開けません。");

            _writeHandle = _readHandle;
            NativeMethods.HidD_SetNumInputBuffers(_readHandle, 64);

            _reading = true;
            _readReady.Reset();
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "HID reader" };
            _readThread.Start();
            if (!_readReady.WaitOne(3000))
            {
                Dispose();
                throw new TimeoutException("HID受信処理の開始がタイムアウトしました。");
            }
        }

        public void Send(byte[] payload, CommandTransport transport, byte reportId)
        {
            if (_writeHandle == null || _writeHandle.IsInvalid)
                throw new InvalidOperationException("HID機器が接続されていません。");
            if (transport == CommandTransport.None || payload.Length == 0)
                return;

            int reportLength = transport == CommandTransport.FeatureReport
                ? _info.FeatureReportLength : _info.OutputReportLength;
            if (reportLength <= 0)
                reportLength = payload.Length + 1;
            if (payload.Length > reportLength - 1)
                throw new InvalidOperationException(
                    "コマンドがHIDレポート長を超えています。対象機種の仕様に合わせた分割送信が必要です。");

            byte[] report = new byte[reportLength];
            report[0] = reportId;
            Buffer.BlockCopy(payload, 0, report, 1, payload.Length);

            if (transport == CommandTransport.FeatureReport)
            {
                if (!NativeMethods.HidD_SetFeature(_writeHandle, report, report.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Feature Report送信に失敗しました。");
            }
            else
            {
                IntPtr writeEvent = NativeMethods.CreateEvent(
                    IntPtr.Zero, true, false, null);
                if (writeEvent == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr reportBuffer = Marshal.AllocHGlobal(report.Length);
                IntPtr overlappedBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(NativeMethods.NATIVE_OVERLAPPED)));
                try
                {
                    Marshal.Copy(report, 0, reportBuffer, report.Length);
                    var overlapped = new NativeMethods.NATIVE_OVERLAPPED
                    {
                        EventHandle = writeEvent
                    };
                    Marshal.StructureToPtr(overlapped, overlappedBuffer, false);
                    int written;
                    bool completed = NativeMethods.WriteFile(_writeHandle, reportBuffer,
                        report.Length, out written, overlappedBuffer);
                    if (!completed)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != NativeMethods.ERROR_IO_PENDING)
                            throw new Win32Exception(error, "Output Report送信に失敗しました。");
                        uint wait = NativeMethods.WaitForSingleObject(writeEvent, 3000);
                        if (wait != NativeMethods.WAIT_OBJECT_0)
                            throw new TimeoutException("Output Report送信がタイムアウトしました。");
                        if (!NativeMethods.GetOverlappedResult(_writeHandle,
                            overlappedBuffer, out written, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                "Output Report送信の完了取得に失敗しました。");
                    }
                    if (written != report.Length)
                        throw new IOException("Output Reportを最後まで送信できませんでした。");
                }
                finally
                {
                    Marshal.FreeHGlobal(overlappedBuffer);
                    Marshal.FreeHGlobal(reportBuffer);
                    NativeMethods.CloseHandle(writeEvent);
                }
            }
        }

        private void ReadLoop()
        {
            int bufferLength = Math.Max(64, (int)_info.InputReportLength);
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            IntPtr overlappedBuffer = Marshal.AllocHGlobal(
                Marshal.SizeOf(typeof(NativeMethods.NATIVE_OVERLAPPED)));
            IntPtr readEvent = NativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
            if (readEvent == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(overlappedBuffer);
                Marshal.FreeHGlobal(buffer);
                RaiseConnectionError(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return;
            }
            try
            {
                while (_reading)
                {
                    NativeMethods.ResetEvent(readEvent);
                    var overlapped = new NativeMethods.NATIVE_OVERLAPPED
                    {
                        EventHandle = readEvent
                    };
                    Marshal.StructureToPtr(overlapped, overlappedBuffer, false);
                    int read;
                    bool completed = NativeMethods.ReadFile(_readHandle, buffer,
                        bufferLength, out read, overlappedBuffer);
                    _readReady.Set();
                    if (!completed)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != NativeMethods.ERROR_IO_PENDING)
                            throw new Win32Exception(error);

                        while (_reading)
                        {
                            uint wait = NativeMethods.WaitForSingleObject(readEvent, 250);
                            if (wait == NativeMethods.WAIT_OBJECT_0)
                                break;
                        }
                        if (!_reading)
                            break;
                        if (!NativeMethods.GetOverlappedResult(_readHandle,
                            overlappedBuffer, out read, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (read > 0)
                    {
                        byte[] report = new byte[read];
                        Marshal.Copy(buffer, report, 0, read);
                        var handler = ReportReceived;
                        if (handler != null)
                            handler(report);
                    }
                }
            }
            catch (Exception ex)
            {
                _readReady.Set();
                if (_reading)
                    RaiseConnectionError(ex.Message);
            }
            finally
            {
                NativeMethods.CloseHandle(readEvent);
                Marshal.FreeHGlobal(overlappedBuffer);
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void RaiseConnectionError(string message)
        {
            var handler = ConnectionError;
            if (handler != null)
                handler(message);
        }

        public void Dispose()
        {
            _reading = false;
            if (_readHandle != null && !_readHandle.IsInvalid)
                NativeMethods.CancelIoEx(_readHandle, IntPtr.Zero);
            if (_readThread != null && _readThread.IsAlive &&
                Thread.CurrentThread != _readThread)
            {
                try { _readThread.Join(1500); } catch { }
            }
            _readThread = null;
            if (_readHandle != null)
            {
                try { _readHandle.Close(); } catch { }
                _readHandle = null;
            }
            if (_writeHandle != null && !_writeHandle.IsClosed)
            {
                try { _writeHandle.Close(); } catch { }
            }
            _writeHandle = null;
        }
    }

    internal static class HidEnumerator
    {
        public static List<HidDeviceInfo> Enumerate()
        {
            Guid hidGuid;
            NativeMethods.HidD_GetHidGuid(out hidGuid);
            IntPtr set = NativeMethods.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            var devices = new List<HidDeviceInfo>();
            if (set == NativeMethods.INVALID_HANDLE_VALUE)
                return devices;

            try
            {
                uint index = 0;
                while (true)
                {
                    var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf(interfaceData);
                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid,
                        index++, ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_NO_MORE_ITEMS)
                            break;
                        continue;
                    }

                    uint required;
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData,
                        IntPtr.Zero, 0, out required, IntPtr.Zero);
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize:
                        // Unicode x86では6、x64では8。旧実装のx86=5はANSI用で、
                        // Unicodeのデバイスパス取得に失敗して全HIDが列挙されなかった。
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData,
                            detail, required, out required, IntPtr.Zero))
                            continue;
                        IntPtr pathPtr = new IntPtr(detail.ToInt64() + 4);
                        string path = Marshal.PtrToStringAuto(pathPtr);
                        HidDeviceInfo info = ReadInfo(path);
                        if (info != null)
                            devices.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { NativeMethods.SetupDiDestroyDeviceInfoList(set); }
            return devices;
        }

        private static HidDeviceInfo ReadInfo(string path)
        {
            var handle = NativeMethods.CreateFile(path, 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;
            try
            {
                var attributes = new NativeMethods.HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(attributes);
                NativeMethods.HidD_GetAttributes(handle, ref attributes);

                var info = new HidDeviceInfo
                {
                    DevicePath = path,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    Product = GetString(handle, NativeMethods.HidD_GetProductString),
                    Manufacturer = GetString(handle, NativeMethods.HidD_GetManufacturerString),
                    SerialNumber = GetString(handle, NativeMethods.HidD_GetSerialNumberString)
                };

                IntPtr preparsed;
                if (NativeMethods.HidD_GetPreparsedData(handle, out preparsed))
                {
                    try
                    {
                        NativeMethods.HIDP_CAPS caps;
                        if (NativeMethods.HidP_GetCaps(preparsed, out caps) >= 0)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            info.FeatureReportLength = caps.FeatureReportByteLength;
                            AddReportIds(info.OutputReportIds, preparsed, 1,
                                caps.NumberOutputValueCaps, true, info.OutputCapabilities);
                            AddReportIds(info.OutputReportIds, preparsed, 1,
                                caps.NumberOutputButtonCaps, false, info.OutputCapabilities);
                            AddReportIds(new List<byte>(), preparsed, 0,
                                caps.NumberInputValueCaps, true, info.InputCapabilities);
                            AddReportIds(new List<byte>(), preparsed, 0,
                                caps.NumberInputButtonCaps, false, info.InputCapabilities);
                        }
                    }
                    finally { NativeMethods.HidD_FreePreparsedData(preparsed); }
                }
                return info;
            }
            finally { handle.Close(); }
        }

        private static void AddReportIds(List<byte> reportIds, IntPtr preparsed,
            int reportType, short capabilityCount, bool valueCaps,
            List<string> capabilityDescriptions)
        {
            if (capabilityCount <= 0)
                return;

            // HIDP_VALUE_CAPS/HIDP_BUTTON_CAPSは32/64bitとも72 byteで、
            // ReportIDは先頭UsagePage(2 byte)の直後にある。
            const int capabilitySize = 72;
            ushort count = (ushort)capabilityCount;
            IntPtr buffer = Marshal.AllocHGlobal(capabilitySize * count);
            try
            {
                int status = valueCaps
                    ? NativeMethods.HidP_GetValueCaps(reportType, buffer, ref count, preparsed)
                    : NativeMethods.HidP_GetButtonCaps(reportType, buffer, ref count, preparsed);
                if (status < 0)
                    return;
                for (int index = 0; index < count; index++)
                {
                    int offset = index * capabilitySize;
                    ushort usagePage = (ushort)Marshal.ReadInt16(buffer, offset);
                    byte reportId = Marshal.ReadByte(buffer, offset + 2);
                    ushort bitField = (ushort)Marshal.ReadInt16(buffer, offset + 4);
                    bool isRange = Marshal.ReadByte(buffer, offset + 12) != 0;
                    ushort bitSize = (ushort)Marshal.ReadInt16(buffer, offset + 18);
                    ushort reportCount = (ushort)Marshal.ReadInt16(buffer, offset + 20);
                    ushort usage = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                    ushort usageMax = isRange
                        ? (ushort)Marshal.ReadInt16(buffer, offset + 58)
                        : usage;
                    if (!reportIds.Contains(reportId))
                        reportIds.Add(reportId);
                    capabilityDescriptions.Add(string.Format(
                        "{0} ID={1:X2} Page={2:X4} Usage={3:X4}-{4:X4} BitSize={5} Count={6} Flags={7:X4}",
                        valueCaps ? "Value" : "Button", reportId, usagePage,
                        usage, usageMax, bitSize, reportCount, bitField));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private delegate bool HidStringReader(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer, int length);

        private static string GetString(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            HidStringReader reader)
        {
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                return reader(handle, buffer, 512) ? Marshal.PtrToStringUni(buffer) : "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static class HidReportParser
    {
        public static string ExtractText(byte[] report)
        {
            if (report == null || report.Length == 0)
                return "";

            int offset = 0;
            int length = report.Length;

            // Honeywell HID POS input report:
            // ID 02 / packet length / flags and status(3 byte) / data.
            if (report[0] == 0x02 && report.Length >= 6)
            {
                int packetLength = report[1];
                offset = 5;
                length = Math.Max(0, Math.Min(packetLength,
                    report.Length - offset));
            }

            var text = new StringBuilder();
            for (int index = offset; index < offset + length; index++)
            {
                byte value = report[index];
                if (value >= 0x20 && value <= 0x7E)
                    text.Append((char)value);
                else if (value == '\r' || value == '\n' || value == '\t')
                    text.Append((char)value);
            }
            return text.ToString();
        }
    }

    internal static class NativeMethods
    {
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 1;
        internal const uint FILE_SHARE_WRITE = 2;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        internal const uint DIGCF_PRESENT = 2;
        internal const uint DIGCF_DEVICEINTERFACE = 0x10;
        internal const int ERROR_NO_MORE_ITEMS = 259;
        internal const int ERROR_IO_PENDING = 997;
        internal const uint WAIT_OBJECT_0 = 0;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public short InputReportByteLength;
            public short OutputReportByteLength;
            public short FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public short NumberLinkCollectionNodes;
            public short NumberInputButtonCaps;
            public short NumberInputValueCaps;
            public short NumberInputDataIndices;
            public short NumberOutputButtonCaps;
            public short NumberOutputValueCaps;
            public short NumberOutputDataIndices;
            public short NumberFeatureButtonCaps;
            public short NumberFeatureValueCaps;
            public short NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NATIVE_OVERLAPPED
        {
            public IntPtr InternalLow;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetAttributes(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, out IntPtr preparsedData);
        [DllImport("hid.dll")]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps,
            ref ushort valueCapsLength, IntPtr preparsedData);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetButtonCaps(int reportType, IntPtr buttonCaps,
            ref ushort buttonCapsLength, IntPtr preparsedData);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        internal static extern bool HidD_GetProductString(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer, int length);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        internal static extern bool HidD_GetManufacturerString(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer, int length);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        internal static extern bool HidD_GetSerialNumberString(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer, int length);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_SetFeature(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_SetNumInputBuffers(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, int numberBuffers);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid,
            IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet,
            IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData,
            uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll")]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer,
            int bytesToRead, out int bytesRead, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer,
            int bytesToWrite, out int bytesWritten, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            IntPtr overlapped, out int bytesTransferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr CreateEvent(
            IntPtr securityAttributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]
        internal static extern bool ResetEvent(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr overlapped);
    }
}
