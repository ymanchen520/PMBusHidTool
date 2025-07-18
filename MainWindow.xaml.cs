using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PMBusHidTool
{
    public partial class MainWindow : Window
    {
        private readonly HidSmbusService _hidService;
        private readonly PmbusService _pmbusService;
        private List<byte> _foundAddresses = new List<byte>();
        private DispatcherTimer _autoRefreshTimer;

        public ObservableCollection<PmbusParameter> PmbusParameters { get; set; }
        public ObservableCollection<KeyValuePair<string, string>> DeviceInfos { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            _hidService = new HidSmbusService();
            _pmbusService = new PmbusService(_hidService);
            
            PmbusParameters = new ObservableCollection<PmbusParameter>();
            PmbusDataGrid.ItemsSource = PmbusParameters;

            DeviceInfos = new ObservableCollection<KeyValuePair<string, string>>();
            DeviceInfoGrid.ItemsSource = DeviceInfos;

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoRefreshTimer.Tick += async (s, e) => await RefreshMonitoringData();

            Log("欢迎使用 PMBus HID 上位机 v3.0。请连接硬件并扫描设备。");
        }
        
        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                LogTextBox.ScrollToEnd();
            });
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            Log("开始扫描设备...");
            SetUiEnabled(false);
            AddressComboBox.Items.Clear();

            await Task.Run(() =>
            {
                if (!_hidService.IsConnected()) _hidService.Connect();
                if (_hidService.IsConnected()) _foundAddresses = _hidService.ScanAddresses();
            });

            if (!_hidService.IsConnected())
            {
                Log("错误: 未找到指定的HID设备。请检查VID/PID和设备连接。");
                MessageBox.Show("未找到指定的HID设备。\n请检查硬件连接，并确认HidSmbusService.cs中的VID/PID是否正确。", "连接错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (_foundAddresses.Any())
            {
                Log($"扫描完成！发现 {_foundAddresses.Count} 个设备: {string.Join(", ", _foundAddresses.Select(a => $"0x{a:X2}"))}");
                foreach (var addr in _foundAddresses)
                {
                    AddressComboBox.Items.Add($"0x{addr:X2}");
                }
                AddressComboBox.SelectedIndex = 0;
            }
            else
            {
                Log("扫描完成，但未在I2C总线上发现任何响应设备。");
            }
            ScanButton.IsEnabled = true;
        }

        private async void AddressComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isDeviceSelected = AddressComboBox.SelectedItem != null;
            SetUiEnabled(isDeviceSelected);

            if (isDeviceSelected)
            {
                _autoRefreshTimer.Start();
                await RefreshAllData();
            }
            else
            {
                _autoRefreshTimer.Stop();
            }
        }

        private async Task RefreshAllData()
        {
            await RefreshMonitoringData();
            await RefreshDeviceInfo();
            await RefreshConfigurationData();
        }

        private async Task RefreshMonitoringData()
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"正在从地址 0x{selectedAddress:X2} 刷新监控数据...");

            var results = await Task.Run(() => _pmbusService.ReadAllMonitoredParameters(selectedAddress));

            UpdateParameter("输出电压", "V", results.Vout);
            UpdateParameter("输出电流", "A", results.Iout);
            UpdateParameter("输入电压", "V", results.Vin);
            UpdateParameter("温度", "°C", results.Temperature);
            UpdateParameter("状态字", "", results.StatusWord);

            if (results.StatusWord.HasValue)
            {
                StatusDecodeTextBox.Text = PmbusStatusDecoder.DecodeStatusWord(results.StatusWord.Value.RawValue);
            }
            else
            {
                StatusDecodeTextBox.Text = "读取状态字失败。";
            }
            Log("监控数据已更新。");
        }

        private async Task RefreshDeviceInfo()
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"正在读取设备信息 0x{selectedAddress:X2}...");

            DeviceInfos.Clear();
            var info = await Task.Run(() => _pmbusService.ReadDeviceInformation(selectedAddress));
            
            DeviceInfos.Add(new KeyValuePair<string, string>("制造商 (MFR_ID)", info.MfrId ?? "N/A"));
            DeviceInfos.Add(new KeyValuePair<string, string>("型号 (MFR_MODEL)", info.MfrModel ?? "N/A"));
            DeviceInfos.Add(new KeyValuePair<string, string>("硬件版本 (MFR_REVISION)", info.MfrRevision ?? "N/A"));
            DeviceInfos.Add(new KeyValuePair<string, string>("序列号 (MFR_SERIAL)", info.MfrSerial ?? "N/A"));
            DeviceInfos.Add(new KeyValuePair<string, string>("PMBus 版本 (PMBUS_REVISION)", info.PmbusRevision ?? "N/A"));
            Log("设备信息已更新。");
        }
        
        private async Task RefreshConfigurationData()
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"正在读取配置限制 0x{selectedAddress:X2}...");

            var voutOvLimit = await Task.Run(() => _pmbusService.ReadVoutOvFaultLimit(selectedAddress));
            VoutOvFaultLimitTextBox.Text = voutOvLimit?.ToString("F2") ?? "N/A";

            var ioutOcLimit = await Task.Run(() => _pmbusService.ReadIoutOcFaultLimit(selectedAddress));
            IoutOcFaultLimitTextBox.Text = ioutOcLimit?.ToString("F2") ?? "N/A";

            var otLimit = await Task.Run(() => _pmbusService.ReadOtFaultLimit(selectedAddress));
            OtFaultLimitTextBox.Text = otLimit?.ToString("F2") ?? "N/A";
            
            Log("配置限制已更新。");
        }

        private void UpdateParameter(string name, string unit, PmbusReadResult? result)
        {
            var param = PmbusParameters.FirstOrDefault(p => p.Name == name);
            if (param == null)
            {
                param = new PmbusParameter { Name = name, Unit = unit };
                PmbusParameters.Add(param);
            }

            if (result.HasValue)
            {
                param.Value = result.Value.ConvertedValue.ToString("F3");
                param.RawValue = $"0x{result.Value.RawValue:X4}";
            }
            else
            {
                param.Value = "读取失败";
                param.RawValue = "N/A";
            }
        }

        private async void ClearFaultsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"发送 CLEAR_FAULTS 到地址 0x{selectedAddress:X2}...");
            bool success = await Task.Run(() => _pmbusService.ClearFaults(selectedAddress));
            Log(success ? "清除故障命令发送成功。" : "清除故障命令发送失败。");
            if(success) await RefreshMonitoringData();
        }

        private async void TurnOnButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"发送 ON 命令到地址 0x{selectedAddress:X2}...");
            bool success = await Task.Run(() => _pmbusService.SetOperation(selectedAddress, PmbusOperation.On));
            Log(success ? "开启命令发送成功。" : "开启命令发送失败。");
        }

        private async void TurnOffButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            Log($"发送 OFF 命令到地址 0x{selectedAddress:X2}...");
            bool success = await Task.Run(() => _pmbusService.SetOperation(selectedAddress, PmbusOperation.Off));
            Log(success ? "关断命令发送成功。" : "关断命令发送失败。");
        }

        private async void SetLimitButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];
            var button = sender as Button;
            if (button == null) return;

            string tag = button.Tag.ToString();
            bool success = false;

            switch (tag)
            {
                case "VOUT_OV":
                    if (double.TryParse(VoutOvFaultLimitTextBox.Text, out double voutLimit))
                    {
                        Log($"设置 VOUT_OV_FAULT_LIMIT 为 {voutLimit}V...");
                        success = await Task.Run(() => _pmbusService.SetVoutOvFaultLimit(selectedAddress, voutLimit));
                    }
                    break;
                case "IOUT_OC":
                    if (double.TryParse(IoutOcFaultLimitTextBox.Text, out double ioutLimit))
                    {
                        Log($"设置 IOUT_OC_FAULT_LIMIT 为 {ioutLimit}A...");
                        success = await Task.Run(() => _pmbusService.SetIoutOcFaultLimit(selectedAddress, ioutLimit));
                    }
                    break;
                case "OT":
                    if (double.TryParse(OtFaultLimitTextBox.Text, out double otLimit))
                    {
                        Log($"设置 OT_FAULT_LIMIT 为 {otLimit}°C...");
                        success = await Task.Run(() => _pmbusService.SetOtFaultLimit(selectedAddress, otLimit));
                    }
                    break;
            }
            Log(success ? "设置成功。" : "设置失败或输入无效。");
            if(success) await RefreshConfigurationData();
        }

        private async void ManualReadButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];

            if (!byte.TryParse(ManualCommandCodeTextBox.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte commandCode))
            {
                MessageBox.Show("命令代码必须是一个有效的16进制数 (例如: 8B)。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log($"手动读取: 地址=0x{selectedAddress:X2}, 命令=0x{commandCode:X2}");
            var result = await Task.Run(() => _pmbusService.ExecuteReadWord(selectedAddress, commandCode));
            if (result.HasValue)
            {
                ManualReadResultTextBox.Text = $"0x{result.Value:X4}";
                Log($"读取成功: 收到 0x{result.Value:X4}");
            }
            else
            {
                ManualReadResultTextBox.Text = "读取失败";
                Log("读取失败。");
            }
        }

        private async void ManualWriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressComboBox.SelectedIndex == -1) return;
            byte selectedAddress = _foundAddresses[AddressComboBox.SelectedIndex];

            if (!byte.TryParse(ManualCommandCodeTextBox.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte commandCode))
            {
                MessageBox.Show("命令代码必须是一个有效的16进制数 (例如: 8B)。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ushort.TryParse(ManualWriteValueTextBox.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
            {
                MessageBox.Show("写入数据必须是一个有效的16进制数 (0000-FFFF)。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log($"手动写入字: 地址=0x{selectedAddress:X2}, 命令=0x{commandCode:X2}, 值=0x{value:X4}");
            bool success = await Task.Run(() => _pmbusService.ExecuteWriteWord(selectedAddress, commandCode, value));
            Log(success ? "写入成功。" : "写入失败。");
        }

        private void SetUiEnabled(bool isEnabled)
        {
            ClearFaultsButton.IsEnabled = isEnabled;
            ManualReadButton.IsEnabled = isEnabled;
            ManualWriteButton.IsEnabled = isEnabled;
            TurnOnButton.IsEnabled = isEnabled;
            TurnOffButton.IsEnabled = isEnabled;
            
            SetVoutOvFaultLimitButton.IsEnabled = isEnabled;
            SetIoutOcFaultLimitButton.IsEnabled = isEnabled;
            SetOtFaultLimitButton.IsEnabled = isEnabled;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _hidService.Disconnect();
            base.OnClosing(e);
        }
    }

    public class PmbusParameter : INotifyPropertyChanged
    {
        private string _value;
        private string _rawValue;
        public string Name { get; set; }
        public string Unit { get; set; }
        public string Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public string RawValue
        {
            get => _rawValue;
            set { _rawValue = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawValue))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
    
    public static class PmbusStatusDecoder
    {
        public static string DecodeStatusWord(ushort status)
        {
            if (status == 0) return "状态正常。No faults or warnings detected.";
            var sb = new StringBuilder();
            if ((status & 0x8000) != 0) sb.AppendLine("Bit 15: VOUT Fault");
            if ((status & 0x4000) != 0) sb.AppendLine("Bit 14: IOUT Fault or IOUT_OC_FAULT");
            if ((status & 0x2000) != 0) sb.AppendLine("Bit 13: VIN Fault or VIN_UV_FAULT");
            if ((status & 0x1000) != 0) sb.AppendLine("Bit 12: MFR_SPECIFIC Fault");
            if ((status & 0x0800) != 0) sb.AppendLine("Bit 11: POWER_GOOD# is negated");
            if ((status & 0x0400) != 0) sb.AppendLine("Bit 10: FANS Fault");
            if ((status & 0x0200) != 0) sb.AppendLine("Bit 9: OTHER Fault");
            if ((status & 0x0100) != 0) sb.AppendLine("Bit 8: Unknown Fault");
            if ((status & 0x0080) != 0) sb.AppendLine("Bit 7: VOUT Overvoltage Fault");
            if ((status & 0x0040) != 0) sb.AppendLine("Bit 6: IOUT Overcurrent Fault");
            if ((status & 0x0020) != 0) sb.AppendLine("Bit 5: VIN Undervoltage Fault");
            if ((status & 0x0010) != 0) sb.AppendLine("Bit 4: Temperature Fault or Warning");
            if ((status & 0x0008) != 0) sb.AppendLine("Bit 3: CML (Comm, Mem, Logic) Fault");
            if ((status & 0x0004) != 0) sb.AppendLine("Bit 2: Other Memory or Logic Fault");
            if ((status & 0x0002) != 0) sb.AppendLine("Bit 1: Busy");
            if ((status & 0x0001) != 0) sb.AppendLine("Bit 0: A specific OFF condition");
            return sb.ToString();
        }
    }
}
