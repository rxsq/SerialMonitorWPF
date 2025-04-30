using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public class ComPortInfo
{
    public string Port { get; set; }
    public string Name { get; set; }
    public string UniqueID { get; set; }
}

public class ComPortScanner
{
    private const int BaudRate = 115200;
    private const int ReadTimeoutMs = 3000;

    /// <summary>
    /// Scans WMI for COM ports, then queries each for its hardcoded Unique ID.
    /// </summary>
    public static List<ComPortInfo> GetAvailableComPorts()
    {
        var portList = new List<ComPortInfo>();
        var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"
        );

        foreach (ManagementObject obj in searcher.Get())
        {
            try
            {
                var name = obj["Name"]?.ToString();
                var portMatch = Regex.Match(name ?? string.Empty, @"\(COM\d+\)");
                if (!portMatch.Success)
                    continue;

                var portName = portMatch.Value.Trim('(', ')');
                var uniqueId = ReadUniqueIdFromPort(portName);

                portList.Add(new ComPortInfo
                {
                    Port = portName,
                    Name = name,
                    UniqueID = uniqueId
                });
            }
            catch
            {
                // ignore ports that fail
            }
        }

        return portList;
    }

    /// <summary>
    /// Opens the given serial port, sends 'I', and reads back the "Unique ID: XXXXXX" line.
    /// </summary>
    public static string ReadUniqueIdFromPort(string portName)
    {
        try
        {
            using (var sp = new SerialPort(portName, 115200))
            {
                sp.NewLine = "\n";
                sp.ReadTimeout = 1000;
                sp.WriteTimeout = 1000;
                sp.DtrEnable = true;
                sp.RtsEnable = false;

                sp.Open();
                Thread.Sleep(1000); // Let Arduino boot and flush

                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();

                byte[] request = new byte[] { 0xCE, 0x00, 0x0A };
                sp.Write(request, 0, request.Length);
                sp.BaseStream.Flush();

                List<byte> buffer = new List<byte>();
                DateTime start = DateTime.Now;

                Console.Write($"[ReadUniqueIdFromPort] {portName} raw response: ");

                while ((DateTime.Now - start).TotalMilliseconds < 2000)
                {
                    try
                    {
                        if (sp.BytesToRead > 0)
                        {
                            byte b = (byte)sp.ReadByte();
                            buffer.Add(b);
                            Console.Write(b.ToString("X2") + " ");

                            if (b == 0x0A) break; // footer
                        }
                    }
                    catch { break; }
                }

                Console.WriteLine(); // newline after hex output

                if (buffer.Count >= 3 && buffer[0] == 0xCD && buffer[buffer.Count - 1] == 0x0A)
                {
                    byte[] idBytes = new byte[buffer.Count - 2];
                    for (int i = 1; i < buffer.Count - 1; i++)
                        idBytes[i - 1] = buffer[i];

                    string result = Encoding.ASCII.GetString(idBytes).Trim();
                    Console.WriteLine($"[ReadUniqueIdFromPort] Parsed ID: {result}");
                    return result;
                }
                else
                {
                    Console.WriteLine("[ReadUniqueIdFromPort] ⚠️ Invalid or incomplete response.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("⚠️ Exception in ReadUniqueIdFromPort: " + ex.Message);
        }

        return null;
    }
}
