using HidSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PMBusHidTool
{
    public class HidSmbusService
    {
        // !!! 用户必须修改的部分 !!!
        private const int VENDOR_ID = 0x04D8;
        private const int PRODUCT_ID = 0xEB42;
        private HidDevice _device;
        private HidStream _stream;
        private readonly object _lock = new object();

        public bool IsConnected() => _device != null && _stream != null && _stream.CanRead && _stream.CanWrite;

        public bool Connect()
        {
            try
            {
                var device = DeviceList.Local.GetHidDevices(VENDOR_ID, PRODUCT_ID).FirstOrDefault();
                if (device != null)
                {
                    if (device.TryOpen(out _stream))
                    {
                        _device = device;
                        _stream.ReadTimeout = 300;
                        return true;
                    }
                }
            }
            catch { /* Ignore */ }
            return false;
        }

        public void Disconnect()
        {
            _stream?.Dispose();
            _stream = null;
            _device = null;
        }

        public List<byte> ScanAddresses()
        {
            var found = new List<byte>();
            if (!IsConnected()) return found;
            lock (_lock)
            {
                for (byte addr = 0x08; addr <= 0x77; addr++)
                {
                    if (PerformWrite(addr, new byte[0])) found.Add(addr);
                }
            }
            return found;
        }
        
        public bool SendByte(byte deviceAddress, byte commandCode)
        {
            lock(_lock) return PerformWrite(deviceAddress, new[] { commandCode });
        }

        public ushort? ReadWord(byte deviceAddress, byte commandCode)
        {
            lock (_lock)
            {
                if (!PerformWrite(deviceAddress, new[] { commandCode })) return null;
                System.Threading.Thread.Sleep(20);
                var data = PerformRead(deviceAddress, 2);
                if (data != null && data.Length == 2) return (ushort)((data[1] << 8) | data[0]);
            }
            return null;
        }

        public bool WriteWord(byte deviceAddress, byte commandCode, ushort value)
        {
            lock (_lock)
            {
                byte[] data = { commandCode, (byte)(value & 0xFF), (byte)(value >> 8) };
                return PerformWrite(deviceAddress, data);
            }
        }
        
        public byte[] BlockRead(byte deviceAddress, byte commandCode)
        {
            lock (_lock)
            {
                if (!PerformWrite(deviceAddress, new[] { commandCode })) return null;
                System.Threading.Thread.Sleep(20);
                return PerformRead(deviceAddress, 32 + 1);
            }
        }

        private byte[] PerformRead(byte deviceAddress, int bytesToRead)
        {
            if (!IsConnected() || bytesToRead > 60) return null;

            var outputReport = new byte[_device.GetMaxOutputReportLength()];
            outputReport[0] = 0x00;
            outputReport[1] = 0x52;
            outputReport[2] = (byte)(deviceAddress << 1 | 0x01);
            outputReport[3] = (byte)bytesToRead;

            try
            {
                _stream.Write(outputReport);
                var inputReport = _stream.Read();
                
                if (inputReport.Length > 2 && inputReport[0] == 0x01)
                {
                    int readLen = inputReport[1];
                    if(readLen > inputReport.Length - 2) readLen = inputReport.Length - 2;
                    return inputReport.Skip(2).Take(readLen).ToArray();
                }
            }
            catch (TimeoutException) { /* Ignore */ }
            return null;
        }

        private bool PerformWrite(byte deviceAddress, byte[] data)
        {
            if (!IsConnected() || data.Length > 60) return false;

            var outputReport = new byte[_device.GetMaxOutputReportLength()];
            outputReport[0] = 0x00;
            outputReport[1] = 0x57;
            outputReport[2] = (byte)(deviceAddress << 1);
            outputReport[3] = (byte)data.Length;
            if (data.Length > 0) Array.Copy(data, 0, outputReport, 4, data.Length);

            try
            {
                _stream.Write(outputReport);
                var inputReport = _stream.Read();
                return inputReport.Length > 0 && inputReport[0] == 0x01;
            }
            catch (TimeoutException) { /* Ignore */ }
            return false;
        }
    }
}
