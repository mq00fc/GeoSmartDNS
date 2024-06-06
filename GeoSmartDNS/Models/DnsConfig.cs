using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GeoSmartDNS.Models
{
    public class DnsConfig
    {
        [JsonPropertyName("proxyServers")]
        public List<ProxyServer> ProxyServers { get; set; }

        [JsonPropertyName("dnsServers")]
        public List<DnsServer> DnsServers { get; set; }

        [JsonPropertyName("rules")]
        public List<Rule> Rules { get; set; }
    }
}
