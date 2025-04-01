using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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
                    serialPort.BaudRate = 9600; // Change this if needed
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
                string incoming = serialPort.ReadLine();
                Dispatcher.Invoke(() =>
                {
                    textBoxOutput.AppendText(incoming + Environment.NewLine);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    textBoxOutput.AppendText("ERROR: " + ex.Message + Environment.NewLine);
                });
            }
        }
    }
}