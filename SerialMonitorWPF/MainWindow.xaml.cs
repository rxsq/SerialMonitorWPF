using SerialMonitorWPF.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Management;
using System.Text.RegularExpressions;
using System.IO;

namespace SerialMonitorWPF
{
    public partial class MainWindow : Window
    {
        private SerialPort laser1;
        private SerialPort laser2;
        private SerialPort sensor1;
        private SerialPort sensor2;

        private readonly HttpClient httpClient = new HttpClient();
        private readonly string apiUrl;
        private readonly int gameId;

        private bool allLightsOn = false;
        private bool ctrlAOn = false;
        private bool ctrlBOn = false;

        private bool isReceiving = false;
        private bool isLogging = false;
        private readonly Dictionary<string, DateTime> logStartTimes = new Dictionary<string, DateTime>();
        private readonly object logFileLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            apiUrl = ConfigurationManager.AppSettings["server"];
            gameId = int.Parse(ConfigurationManager.AppSettings["gameId"]);
            buttonStartStop.Content = "Start Receiving";

            textBoxRows.Text = "8";
            textBoxCols.Text = "6";

            buttonRefresh.Click += (s, e) => ReloadPorts();
            buttonConnect.Click += ButtonConnect_Click;
            buttonSendRaw.Click += ButtonSendRaw_Click;

            buttonAllOn.Click += ButtonAllOn_Click;
            buttonCtrlAOn.Click += ButtonCtrlAOn_Click;
            buttonCtrlBOn.Click += ButtonCtrlBOn_Click;

            buttonLogSignal.Click += ButtonLogSignal_Click;
            buttonStartStop.Click += ButtonStartStop_Click;
        }

        private async void ReloadPorts()
        {
            DisconnectAllPorts();
            statusText.Text = "";
            loadingBar.Visibility = Visibility.Visible;

            try
            {
                await Task.Delay(100); // slight delay to let UI update

                var response = await httpClient.GetAsync($"{apiUrl}/devices?GameID={gameId}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var devices = JsonConvert.DeserializeObject<List<Device>>(json);

                // 2) enumerate COM ports + read each board’s UniqueID
                var wmiPorts = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'").Get();

                var available = new List<ComPortInfo>();

                foreach (ManagementObject obj in wmiPorts)
                {
                    try
                    {
                        var name = obj["Name"]?.ToString();
                        var match = Regex.Match(name ?? "", @"\(COM\d+\)");
                        if (!match.Success) continue;

                        var port = match.Value.Trim('(', ')');
                        var uid = await Task.Run(() => ComPortScanner.ReadUniqueIdFromPort(port));

                        available.Add(new ComPortInfo
                        {
                            Port = port,
                            Name = name,
                            UniqueID = uid
                        });
                    }
                    catch { }
                }
                foreach (var info in available)
                {
                    if (string.IsNullOrEmpty(info.UniqueID))
                    {
                        AppendStatus($"No UniqueID returned on {info.Port}");
                        continue;
                    }

                    var id = info.UniqueID.ToUpperInvariant();

                    var device = devices.FirstOrDefault(d =>
                        d.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase));

                    if (device == null)
                    {
                        AppendStatus($"Unrecognized ID {id} on {info.Port}");
                        continue;
                    }

                    var port = new SerialPort(info.Port, 115200, Parity.None, 8, StopBits.One)
                    {
                        Handshake = Handshake.None,
                        NewLine = "\n",
                        ReadTimeout = 3000
                    };

                    port.DataReceived += SerialPort_DataReceived;
                    port.Open();

                    switch (device.DeviceType.ToLowerInvariant())
                    {
                        case "sensor1": sensor1 = port; break;
                        case "sensor2": sensor2 = port; break;
                        case "laser1": laser1 = port; break;
                        case "laser2": laser2 = port; break;
                        default:
                            AppendStatus($"Unknown device type {device.DeviceType} for ID {id}");
                            port.Close();
                            continue;
                    }

                    AppendStatus($"Connected {device.DeviceType} (ID={id}) → {info.Port}");
                }

                buttonConnect.Content = "Disconnect";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading ports: " + ex.Message);
            }
            finally
            {
                loadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sensor1 == null && sensor2 == null && laser1 == null && laser2 == null)
                ReloadPorts();
            else
                DisconnectAllPorts();
        }

        private void DisconnectAllPorts()
        {
            DisposeSerial(sensor1); sensor1 = null;
            DisposeSerial(sensor2); sensor2 = null;
            DisposeSerial(laser1); laser1 = null;
            DisposeSerial(laser2); laser2 = null;

            buttonConnect.Content = "Connect Devices";
            statusText.Text = "Disconnected";
        }

        private void DisposeSerial(SerialPort port)
        {
            try
            {
                if (port != null && port.IsOpen)
                {
                    port.Close();
                    port.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendStatus("Error closing port: " + ex.Message);
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var port = sender as SerialPort;
                if (port == null) return;

                while (port.BytesToRead >= 8)
                {
                    byte[] buffer = new byte[port.BytesToRead];
                    port.Read(buffer, 0, buffer.Length);

                    for (int i = 0; i <= buffer.Length - 8; i++)
                    {
                        // Check for valid header + footer
                        byte header = buffer[i];
                        byte footer = buffer[i + 7];

                        if ((header == 0xCA || header == 0xCB) && footer == 0x0A)
                        {
                            byte[] packet = new byte[8];
                            Array.Copy(buffer, i, packet, 0, 8);

                            Dispatcher.Invoke(() =>
                            {
                                //AppendStatus("Valid: " + BitConverter.ToString(packet));
                                ProcessDataPacket(packet, port.PortName);
                            });

                            i += 7; // Skip over the full packet
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendStatus("Serial read error: " + ex.Message));
            }
        }

        private void ProcessDataPacket(byte[] data, string portName)
        {

            if (isLogging && logStartTimes.TryGetValue(portName, out var start))
            {
                var elapsed = DateTime.UtcNow - start;
                LogResponseTime(portName, elapsed);
                logStartTimes.Remove(portName);
                if (logStartTimes.Count == 0)
                    isLogging = false;
            }

            try
            {
                List<int> cutLasers;
                TextBox targetTextBox;

                if (data[0] == 0xCA)
                {
                    cutLasers = GetCutLasers(data, 0xCA);
                    targetTextBox = textBoxOutputA;
                }
                else if (data[0] == 0xCB)
                {
                    cutLasers = GetCutLasers(data, 0xCB);
                    targetTextBox = textBoxOutputB;
                }
                else
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    int rows = 6;
                    int cols = 8;
                    int.TryParse(textBoxRows.Text, out rows);
                    int.TryParse(textBoxCols.Text, out cols);

                    StringBuilder displayBuilder = new StringBuilder();

                    displayBuilder.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] Sensor Grid Data:");
                    displayBuilder.Append("Raw Data (HEX): ");
                    foreach (var b in data)
                        displayBuilder.Append(b.ToString("X2") + " ");
                    displayBuilder.AppendLine();

                    displayBuilder.AppendLine("\nBinary Grid:");
                    displayBuilder.Append("    ");
                    for (int col = 0; col < cols; col++)
                        displayBuilder.Append(string.Format("{0,3}", col));
                    displayBuilder.AppendLine();

                    for (int row = 0; row < rows; row++)
                    {
                        displayBuilder.Append(string.Format("{0,2} ", row));
                        for (int col = 0; col < cols; col++)
                        {
                            int index = row * cols + col;
                            if (index < cutLasers.Count)
                                displayBuilder.Append(string.Format("{0,3}", cutLasers[index]));
                            else
                                displayBuilder.Append("  -");
                        }
                        displayBuilder.AppendLine();
                    }

                    displayBuilder.AppendLine("----------------------------------");

                    if (targetTextBox.Text.Length > 5000)
                        targetTextBox.Text = targetTextBox.Text.Substring(targetTextBox.Text.Length - 4000);

                    targetTextBox.AppendText(displayBuilder.ToString());
                    targetTextBox.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendStatus("Grid parse error: " + ex.Message));
            }
        }

        private List<int> GetCutLasers(byte[] message, byte headerByte)
        {
            var cutLasers = new List<int>();

            if (message[0] != headerByte || message[message.Length - 1] != 0x0A)
                return cutLasers;

            for (int byteIndex = 1; byteIndex < 7; byteIndex++)
            {
                byte currentByte = message[byteIndex];
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    int bit = (currentByte >> bitIndex) & 0x1;
                    cutLasers.Add(bit);
                }
            }

            while (cutLasers.Count < 48)
                cutLasers.Add(0);

            return cutLasers;
        }

        private void ButtonSendRaw_Click(object sender, RoutedEventArgs e)
        {
            if (laser1 == null && laser2 == null)
            {
                MessageBox.Show("No laser ports connected.");
                return;
            }

            try
            {
                string[] tokens = textBoxRawHex.Text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                List<byte> data = new List<byte>();
                foreach (string token in tokens)
                    data.Add(Convert.ToByte(token, 16));
                byte[] raw = data.ToArray();

                if (laser1 != null && laser1.IsOpen)
                    laser1.Write(raw, 0, raw.Length);
                if (laser2 != null && laser2.IsOpen)
                    laser2.Write(raw, 0, raw.Length);
                if (sensor1 != null && sensor1.IsOpen)
                    sensor1.Write(raw, 0, raw.Length);
                if (sensor2 != null && sensor2.IsOpen)
                    sensor2.Write(raw, 0, raw.Length);

                AppendStatus("Sent Raw: " + BitConverter.ToString(raw));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error sending raw data: " + ex.Message);
            }
        }

        private void AppendStatus(string message)
        {
            var lines = statusText.Text
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            lines.Add(message);

            // Keep only the last 5 lines
            while (lines.Count > 5)
                lines.RemoveAt(0);

            statusText.Text = string.Join("\n", lines);
        }

        private void ButtonAllOn_Click(object sender, RoutedEventArgs e)
        {
            if (!allLightsOn)
            {
                TurnOnAllLasers();
                buttonAllOn.Content = "All Lights Off";
                allLightsOn = true;
            }
            else
            {
                TurnOffAllLasers();
                buttonAllOn.Content = "All Lights On";
                allLightsOn = false;
            }
        }

        private void ButtonCtrlAOn_Click(object sender, RoutedEventArgs e)
        {
            if (laser1 == null || !laser1.IsOpen) return;

            byte[] cmd = ctrlAOn ? BuildLaserOffCommand(0xFA) : BuildLaserOnCommand(0xFA);
            laser1.Write(cmd, 0, cmd.Length);
            ctrlAOn = !ctrlAOn;
            buttonCtrlAOn.Content = ctrlAOn ? "Controller A Off" : "Controller A On";
        }

        private void ButtonCtrlBOn_Click(object sender, RoutedEventArgs e)
        {
            if (laser2 == null || !laser2.IsOpen) return;

            byte[] cmd = ctrlBOn ? BuildLaserOffCommand(0xFB) : BuildLaserOnCommand(0xFB);
            laser2.Write(cmd, 0, cmd.Length);
            ctrlBOn = !ctrlBOn;
            buttonCtrlBOn.Content = ctrlBOn ? "Controller B Off" : "Controller B On";
        }

        private void ButtonLogSignal_Click(object sender, RoutedEventArgs e)
        {
            if ((sensor1 == null || !sensor1.IsOpen) &&
                (sensor2 == null || !sensor2.IsOpen))
            {
                MessageBox.Show("No sensors connected.");
                return;
            }

            isLogging = true;
            logStartTimes.Clear();
            var now = DateTime.UtcNow;

            if (sensor1 != null && sensor1.IsOpen)
                logStartTimes[sensor1.PortName] = now;

            var ping = new byte[] { 0xCC, 0xAA, 0x0A };
            TrySendToSensor(sensor1, ping);

            if (sensor2 != null && sensor2.IsOpen)
                logStartTimes[sensor2.PortName] = now;

            TrySendToSensor(sensor2, ping);

            AppendStatus("Started logging signal");
        }

        private void ButtonStartStop_Click(object sender, RoutedEventArgs e)
        {
            isReceiving = !isReceiving;

            // Build the 3-byte command: [CC, dataByte, 0A]
            byte[] command = new byte[] { 0xCC, isReceiving ? (byte)0xAA : (byte)0x00, 0x0A };

            // Send the command to all connected sensors
            TrySendToSensor(sensor1, command);
            TrySendToSensor(sensor2, command);

            buttonStartStop.Content = isReceiving ? "Stop Receiving" : "Start Receiving";
            AppendStatus(isReceiving ? "Sent CC AA 0A (start receiving)" : "Sent CC 00 0A (stop receiving)");
        }

        private void TrySendToSensor(SerialPort port, byte[] data)
        {
            try
            {
                if (port != null && port.IsOpen)
                {
                    port.DiscardInBuffer(); // clear any junk input
                    port.DiscardOutBuffer(); // clear outgoing queue

                    // Send command cleanly
                    port.Write(data, 0, data.Length);
                    port.BaseStream.Flush(); // ensure write goes immediately

                    // Delay to allow Arduino to handle command before sending next
                    //System.Threading.Thread.Sleep(20);
                }
            }
            catch (Exception ex)
            {
                AppendStatus($"Failed to send to sensor on {port?.PortName}: {ex.Message}");
            }
        }

        private void TurnOnAllLasers()
        {
            var cmdA = BuildLaserOnCommand(0xFA);
            var cmdB = BuildLaserOnCommand(0xFB);

            if (laser1 != null && laser1.IsOpen)
                laser1.Write(cmdA, 0, cmdA.Length);

            System.Threading.Thread.Sleep(50); // small delay

            if (laser2 != null && laser2.IsOpen)
                laser2.Write(cmdB, 0, cmdB.Length);
        }

        private void TurnOffAllLasers()
        {
            var cmdA = BuildLaserOffCommand(0xFA);
            var cmdB = BuildLaserOffCommand(0xFB);

            if (laser1 != null && laser1.IsOpen)
                laser1.Write(cmdA, 0, cmdA.Length);

            System.Threading.Thread.Sleep(50);

            if (laser2 != null && laser2.IsOpen)
                laser2.Write(cmdB, 0, cmdB.Length);
        }

        private byte[] BuildLaserOnCommand(byte controllerId)
        {
            return new byte[] { controllerId, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0A };
        }

        private byte[] BuildLaserOffCommand(byte controllerId)
        {
            return new byte[] { controllerId, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A };
        }

        private void LogResponseTime(string portName, TimeSpan responseTime)
        {
            const string path = "ResponseTimes.log";

            var line = $"{DateTime.UtcNow:O},{portName},{responseTime.TotalMilliseconds}";
            lock (logFileLock)
                File.AppendAllText(path, line + Environment.NewLine);

            AppendStatus($"Logged response for {portName}: {responseTime.TotalMilliseconds} ms");
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            DisconnectAllPorts();
            base.OnClosing(e);
        }
    }
}
