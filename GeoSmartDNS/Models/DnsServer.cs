using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GeoSmartDNS.Models
{
    public class DnsServer
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("proxy")]
        public string? Proxy { get; set; }

        [JsonPropertyName("dnssecValidation")]
        public bool DnssecValidation { get; set; }

        [JsonPropertyName("forwarderProtocol")]
        public string ForwarderProtocol { get; set; }

        [JsonPropertyName("forwarderAddresses")]
        public List<string> ForwarderAddresses { get; set; }
    }
}
