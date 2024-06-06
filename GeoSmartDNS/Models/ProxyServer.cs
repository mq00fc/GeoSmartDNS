using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GeoSmartDNS.Models
{
    public class ProxyServer
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("proxyAddress")]
        public string ProxyAddress { get; set; }

        [JsonPropertyName("proxyPort")]
        public int ProxyPort { get; set; }

        [JsonPropertyName("proxyUsername")]
        public string? ProxyUsername { get; set; }

        [JsonPropertyName("proxyPassword")]
        public string? proxyPassword { get; set; }
    }
}
