using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GeoSmartDNS.Models
{
    public class Rule
    {
        [JsonPropertyName("domain")]
        public List<string> Domain { get; set; }

        [JsonPropertyName("dnsServer")]
        public string DnsServer { get; set; }
    }
}
