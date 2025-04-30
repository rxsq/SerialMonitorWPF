using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialMonitorWPF.Models
{
    public class Device
    {
        public int Id { get; set; }
        public int GameID { get; set; }
        public string DeviceId { get; set; }
        public string DeviceType { get; set; }
        public string ComPort { get; set; }
    }
}
