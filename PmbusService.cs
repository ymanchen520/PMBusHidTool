using System;
using System.Text;

namespace PMBusHidTool
{
    public struct MonitoredParameters
    {
        public PmbusReadResult? Vout;
        public PmbusReadResult? Iout;
        public PmbusReadResult? Vin;
        public PmbusReadResult? Temperature;
        public PmbusReadResult? StatusWord;
    }

    public struct DeviceInformation
    {
        public string MfrId;
        public string MfrModel;
        public string MfrRevision;
        public string MfrSerial;
        public string PmbusRevision;
    }

    public struct PmbusReadResult
    {
        public double ConvertedValue;
        public ushort RawValue;
    }

    public enum PmbusOperation : byte
    {
        Off = 0x00,
        On = 0x80
    }

    public class PmbusService
    {
        private readonly HidSmbusService _smbus;

        #region PMBus Command Codes
        private const byte OPERATION_CMD = 0x01;
        private const byte CLEAR_FAULTS_CMD = 0x03;
        public const byte VOUT_COMMAND_CMD = 0x21;
        private const byte VOUT_OV_FAULT_LIMIT_CMD = 0x40;
        private const byte IOUT_OC_FAULT_LIMIT_CMD = 0x46;
        private const byte OT_FAULT_LIMIT_CMD = 0x4F;
        private const byte STATUS_WORD_CMD = 0x79;
        private const byte READ_VIN_CMD = 0x88;
        private const byte READ_VOUT_CMD = 0x8B;
        private const byte READ_IOUT_CMD = 0x8C;
        private const byte READ_TEMPERATURE_1_CMD = 0x8D;
        private const byte PMBUS_REVISION_CMD = 0x98;
        private const byte MFR_ID_CMD = 0x99;
        private const byte MFR_MODEL_CMD = 0x9A;
        private const byte MFR_REVISION_CMD = 0x9B;
        private const byte MFR_SERIAL_CMD = 0x9E;
        private const byte VOUT_MODE_CMD = 0x20;
        #endregion

        public PmbusService(HidSmbusService smbus)
        {
            _smbus = smbus;
        }
        
        public MonitoredParameters ReadAllMonitoredParameters(byte deviceAddress)
        {
            return new MonitoredParameters
            {
                Vout = ReadVout(deviceAddress),
                Iout = ReadIout(deviceAddress),
                Vin = ReadVin(deviceAddress),
                Temperature = ReadTemperature(deviceAddress),
                StatusWord = ReadStatusWord(deviceAddress)
            };
        }

        public DeviceInformation ReadDeviceInformation(byte deviceAddress)
        {
            return new DeviceInformation
            {
                MfrId = ReadString(deviceAddress, MFR_ID_CMD),
                MfrModel = ReadString(deviceAddress, MFR_MODEL_CMD),
                MfrRevision = ReadString(deviceAddress, MFR_REVISION_CMD),
                MfrSerial = ReadString(deviceAddress, MFR_SERIAL_CMD),
                PmbusRevision = ReadPmbusRevision(deviceAddress)
            };
        }

        public bool ClearFaults(byte deviceAddress) => _smbus.SendByte(deviceAddress, CLEAR_FAULTS_CMD);
        public bool SetOperation(byte deviceAddress, PmbusOperation operation) => _smbus.WriteWord(deviceAddress, OPERATION_CMD, (ushort)operation);

        public double? ReadVoutOvFaultLimit(byte deviceAddress) => ConvertFromLinear11(deviceAddress, VOUT_OV_FAULT_LIMIT_CMD)?.ConvertedValue;
        public bool SetVoutOvFaultLimit(byte deviceAddress, double limit) => WriteLinear11(deviceAddress, VOUT_OV_FAULT_LIMIT_CMD, limit);
        
        public double? ReadIoutOcFaultLimit(byte deviceAddress) => ConvertFromLinear11(deviceAddress, IOUT_OC_FAULT_LIMIT_CMD)?.ConvertedValue;
        public bool SetIoutOcFaultLimit(byte deviceAddress, double limit) => WriteLinear11(deviceAddress, IOUT_OC_FAULT_LIMIT_CMD, limit);

        public double? ReadOtFaultLimit(byte deviceAddress) => ConvertFromLinear11(deviceAddress, OT_FAULT_LIMIT_CMD)?.ConvertedValue;
        public bool SetOtFaultLimit(byte deviceAddress, double limit) => WriteLinear11(deviceAddress, OT_FAULT_LIMIT_CMD, limit);
        
        public ushort? ExecuteReadWord(byte deviceAddress, byte commandCode) => _smbus.ReadWord(deviceAddress, commandCode);
        public bool ExecuteWriteWord(byte deviceAddress, byte commandCode, ushort value) => _smbus.WriteWord(deviceAddress, commandCode, value);
        
        private PmbusReadResult? ReadVout(byte deviceAddress)
        {
            var modeByte = _smbus.ReadWord(deviceAddress, VOUT_MODE_CMD);
            if (!modeByte.HasValue) return null;
            sbyte exponentN = (sbyte)(modeByte.Value & 0x1F);
            if ((exponentN & 0x10) != 0) exponentN = (sbyte)(exponentN | 0xE0);
            return ConvertFromLinear16(deviceAddress, READ_VOUT_CMD, exponentN);
        }
        private PmbusReadResult? ReadIout(byte deviceAddress) => ConvertFromLinear11(deviceAddress, READ_IOUT_CMD);
        private PmbusReadResult? ReadVin(byte deviceAddress) => ConvertFromLinear11(deviceAddress, READ_VIN_CMD);
        private PmbusReadResult? ReadTemperature(byte deviceAddress) => ConvertFromLinear11(deviceAddress, READ_TEMPERATURE_1_CMD);
        private PmbusReadResult? ReadStatusWord(byte deviceAddress)
        {
            var rawValue = _smbus.ReadWord(deviceAddress, STATUS_WORD_CMD);
            if (!rawValue.HasValue) return null;
            return new PmbusReadResult { RawValue = rawValue.Value, ConvertedValue = rawValue.Value };
        }
        private string ReadString(byte deviceAddress, byte commandCode)
        {
            var data = _smbus.BlockRead(deviceAddress, commandCode);
            if (data == null || data.Length < 1) return null;
            int length = data[0];
            if (length > data.Length - 1) length = data.Length - 1;
            return Encoding.ASCII.GetString(data, 1, length);
        }
        private string ReadPmbusRevision(byte deviceAddress)
        {
            var data = _smbus.ReadWord(deviceAddress, PMBUS_REVISION_CMD);
            if (!data.HasValue) return null;
            byte rev = (byte)data.Value;
            return $"{(rev >> 4) & 0x0F}.{rev & 0x0F}";
        }
        
        private PmbusReadResult? ConvertFromLinear16(byte deviceAddress, byte commandCode, sbyte exponentN)
        {
            var rawValue = _smbus.ReadWord(deviceAddress, commandCode);
            if (!rawValue.HasValue) return null;
            double convertedValue = rawValue.Value * Math.Pow(2, exponentN);
            return new PmbusReadResult { RawValue = rawValue.Value, ConvertedValue = convertedValue };
        }

        private PmbusReadResult? ConvertFromLinear11(byte deviceAddress, byte commandCode)
        {
            var rawValue = _smbus.ReadWord(deviceAddress, commandCode);
            if (!rawValue.HasValue) return null;
            
            short mantissa = (short)(rawValue.Value & 0x07FF);
            if ((mantissa & 0x0400) != 0) mantissa = (short)(mantissa | 0xF800);

            sbyte exponent = (sbyte)((rawValue.Value >> 11) & 0x1F);
            if ((exponent & 0x10) != 0) exponent = (sbyte)(exponent | 0xE0);
            
            double convertedValue = mantissa * Math.Pow(2, exponent);
            return new PmbusReadResult { RawValue = rawValue.Value, ConvertedValue = convertedValue };
        }

        private bool WriteLinear11(byte deviceAddress, byte commandCode, double value)
        {
            const int exponent = -2;
            short mantissa = (short)Math.Round(value / Math.Pow(2, exponent));
            ushort rawValue = (ushort)mantissa;
            return _smbus.WriteWord(deviceAddress, commandCode, rawValue);
        }
    }
}
