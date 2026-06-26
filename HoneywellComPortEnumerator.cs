using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace HonHidVerifier
{
    internal static class HoneywellComPortEnumerator
    {
        public static string[] GetPorts()
        {
            var ports = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSerialCommPorts(ports);
            AddEnumPorts(ports);
            string[] result = new string[ports.Count];
            ports.CopyTo(result);
            return result;
        }

        private static void AddSerialCommPorts(SortedSet<string> ports)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DEVICEMAP\SERIALCOMM"))
            {
                if (key == null)
                    return;

                foreach (string name in key.GetValueNames())
                {
                    if (!IsHoneywellText(name))
                        continue;
                    AddPort(ports, key.GetValue(name) as string);
                }
            }
        }

        private static void AddEnumPorts(SortedSet<string> ports)
        {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum"))
            {
                if (root == null)
                    return;
                ScanEnumKey(root, "", ports);
            }
        }

        private static void ScanEnumKey(RegistryKey key, string inheritedText,
            SortedSet<string> ports)
        {
            string text = inheritedText + " " + KeyText(key);
            using (RegistryKey parameters = key.OpenSubKey("Device Parameters"))
            {
                if (parameters != null)
                {
                    string port = parameters.GetValue("PortName") as string;
                    if (IsComPort(port) && IsHoneywellText(text + " " + KeyText(parameters)))
                        AddPort(ports, port);
                }
            }

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                using (RegistryKey subKey = key.OpenSubKey(subKeyName))
                {
                    if (subKey != null)
                        ScanEnumKey(subKey, text + " " + subKeyName, ports);
                }
            }
        }

        private static string KeyText(RegistryKey key)
        {
            var text = "";
            foreach (string name in key.GetValueNames())
            {
                object value = key.GetValue(name);
                if (value is string)
                    text += " " + name + " " + (string)value;
                else if (value is string[])
                    text += " " + name + " " + string.Join(" ", (string[])value);
            }
            return text;
        }

        private static bool IsHoneywellText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.IndexOf("VID_0C2E", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("VID_0536", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("honeywell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("hand held", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("metrologic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("barcode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("scanner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsComPort(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                return false;

            for (int index = 3; index < value.Length; index++)
            {
                if (!char.IsDigit(value[index]))
                    return false;
            }
            return value.Length > 3;
        }

        private static void AddPort(SortedSet<string> ports, string port)
        {
            if (IsComPort(port))
                ports.Add(port);
        }
    }
}
