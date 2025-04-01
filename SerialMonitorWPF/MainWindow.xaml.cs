using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SerialMonitorWPF
{
    public partial class MainWindow : Window
    {
        private SerialPort serialPort = new SerialPort();

        public MainWindow()
        {
            InitializeComponent();
            LoadPorts();
            buttonRefresh.Click += (s, e) => LoadPorts();
            buttonConnect.Click += ButtonConnect_Click;
            serialPort.DataReceived += SerialPort_DataReceived;
        }

        private void LoadPorts()
        {
            comboBoxPorts.ItemsSource = SerialPort.GetPortNames();
            if (comboBoxPorts.Items.Count > 0)
                comboBoxPorts.SelectedIndex = 0;
        }

        private void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                try
                {
                    serialPort.PortName = comboBoxPorts.SelectedItem.ToString();
                    serialPort.BaudRate = 115200; // Change this if needed
                    serialPort.Open();
                    buttonConnect.Content = "Disconnect";
                    statusText.Text = $"Connected to {serialPort.PortName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to connect: {ex.Message}");
                }
            }
            else
            {
                serialPort.Close();
                buttonConnect.Content = "Connect";
                statusText.Text = "Disconnected";
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // Assume incoming data is a line like "CB 00 00 00 00 00 00 0A"
                string incoming = serialPort.ReadLine().Trim();
                ProcessDataPacket(incoming);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    textBoxOutput.AppendText("ERROR: " + ex.Message + Environment.NewLine);
                });
            }
        }

        private void ProcessDataPacket(string data)
        {
            // Split the incoming line by spaces or commas.
            string[] tokens = data.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 8)
            {
                Dispatcher.Invoke(() =>
                {
                    textBoxOutput.AppendText("Invalid data packet: " + data + Environment.NewLine);
                });
                return;
            }
            // tokens[0] is header and tokens[7] is footer; ignore them.
            List<int> sensorBytes = new List<int>();
            for (int i = 1; i <= 6; i++)
            {
                if (int.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out int value))
                {
                    sensorBytes.Add(value);
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        textBoxOutput.AppendText("Failed to parse token: " + tokens[i] + Environment.NewLine);
                    });
                    return;
                }
            }

            // Convert the 6 sensor bytes into a 48-bit string.
            // Note: Each byte is packed with bits in LSB-to-MSB order.
            StringBuilder bitString = new StringBuilder();
            foreach (int b in sensorBytes)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    int bitVal = (b >> bit) & 0x1;
                    bitString.Append(bitVal);
                }
            }

            // Read the rows and columns from the UI inputs.
            int rows = 6, cols = 8;
            if (!int.TryParse(textBoxRows.Text, out rows))
                rows = 6;
            if (!int.TryParse(textBoxCols.Text, out cols))
                cols = 8;

            // Layout the bit string in a grid (only display the first rows*cols bits).
            int totalBits = bitString.Length;
            int displayBits = Math.Min(totalBits, rows * cols);
            StringBuilder displayBuilder = new StringBuilder();
            displayBuilder.AppendLine("Sensor Grid:");
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int index = r * cols + c;
                    if (index < displayBits)
                        displayBuilder.Append(bitString[index] + " ");
                    else
                        displayBuilder.Append("  ");
                }
                displayBuilder.AppendLine();
            }

            Dispatcher.Invoke(() =>
            {
                textBoxOutput.AppendText(displayBuilder.ToString() + Environment.NewLine);
            });
        }
    }
}
