using System.Diagnostics;
using System.Text.RegularExpressions;
using GeoSmartDNS.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace GeoSmartDNS.Implement
{
    public class GeoSiteService : IGeoSiteService
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<GeoSiteService> _logger;
        private readonly IOptions<DnsConfig> _dnsConfig;

        //DnsServer
        private Dictionary<string, NameServerAddress[]> _dnsServerDic = new Dictionary<string, NameServerAddress[]>(StringComparer.InvariantCultureIgnoreCase);
        //GeoSite
        private Dictionary<string, GeoSite> _geoDictionary = new Dictionary<string, GeoSite>(StringComparer.InvariantCultureIgnoreCase);
        public GeoSiteService(IMemoryCache memoryCache,
                              ILogger<GeoSiteService> logger,
                              IOptions<DnsConfig> dnsConfig)
        {
            _memoryCache = memoryCache;
            _logger = logger;
            _dnsConfig = dnsConfig;
        }

        private void Installation()
        {
            //dns上游服务器安装
            foreach (var dnsServer in _dnsConfig.Value.DnsServers)
            {
                var _dnsServers = new List<NameServerAddress>();
                DnsTransportProtocol protocol = (DnsTransportProtocol)Enum.Parse(typeof(DnsTransportProtocol), dnsServer.ForwarderProtocol, true);
                foreach (var dns in dnsServer.ForwarderAddresses)
                {
                    var nameServer = NameServerAddress.Parse(dns);
             
                    if (nameServer.Protocol != protocol)
                        nameServer = nameServer.ChangeProtocol(protocol);

                    _dnsServers.Add(nameServer);
                }
                _dnsServerDic[dnsServer.Name] = _dnsServers.ToArray();
            }

        }

        public async Task LoadGeoSiteFile(string path)
        {
            await LoadGeoSiteFile(await File.ReadAllBytesAsync(path));
        }


        public async Task LoadGeoSiteFile(byte[] bytes)
        {
            GeoSiteList geoSiteList = GeoSiteParse.ParseGeoSiteList(bytes);
            foreach (var item in geoSiteList.Entries)
            {
                _geoDictionary[item.CountryCode] = item;
            }
            Installation();
        }




        public bool IsMatchingDomain(string domain, params string[] countryCodes)
        {
            foreach (var countryCode in countryCodes)
            {
                if (!_geoDictionary.TryGetValue(countryCode, out var geoSite))
                {
                    _logger.LogWarning($"GeoSite:{countryCode} no exits!");
                    continue;
                }

                var countryDomains = geoSite?.Domains;
                if (countryDomains == null || !countryDomains.Any())
                {
                    continue;
                }

                foreach (var d in countryDomains)
                {
                    bool isMatch = d.Type switch
                    {
                        DomainType.Regex => Regex.IsMatch(domain, d.Value),
                        DomainType.Plain => domain.Contains(d.Value),
                        DomainType.Full => domain.Equals(d.Value),
                        DomainType.RootDomain => domain.EndsWith(d.Value),
                        _ => false
                    };

                    if (isMatch)
                    {
                        return true;
                    }
                }
            }
            return false;
        }


        public DnsClient GetDnsClient(string domain)
        {
            Stopwatch sw = Stopwatch.StartNew();
            var dnsName = GetDnsNameServer(domain);
            long s = sw.ElapsedMilliseconds;
            sw.Stop();
            var nameServers = _dnsServerDic[dnsName];
            if(nameServers == null)
            {
                throw new Exception($"DnsName:{dnsName} Undefined!");
            }
            _logger.LogInformation($"域名:{domain}\t命中规则:{dnsName}，耗时:{s}");
            var config = _dnsConfig.Value.DnsServers.FirstOrDefault(x => x.Name == dnsName);
            var dnsClient = new DnsClient(nameServers);
            dnsClient.Concurrency = nameServers.Length;
            dnsClient.Retries = 5;
            dnsClient.Timeout = 2000;
            dnsClient.Cache = new DnsCache();
            if (!string.IsNullOrEmpty(config.Proxy))
            {
                var proxyServer = _dnsConfig.Value.ProxyServers.FirstOrDefault(x => x.Name == config.Proxy);
                if(proxyServer != null)
                {
                    dnsClient.Proxy = NetProxy.CreateSocksProxy(proxyServer.ProxyAddress,proxyServer.ProxyPort);
                }
            }
            return dnsClient;
        }


        public string GetDnsNameServer(string domain)
        {
            var rules = _dnsConfig.Value.Rules;
            foreach (var rule in rules)
            {
                List<string> geosites = new List<string>();
                foreach (var item in rule.Domain)
                {
                    //geosite
                    if (item.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                    {
                        var geosite = item.Substring(8);
                        geosites.Add(geosite);
                        continue;
                    }
                    //前缀匹配
                    if (item.StartsWith("prefix:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parameter = item.Substring(7);
                        if (domain.StartsWith(parameter, StringComparison.OrdinalIgnoreCase))
                        {
                            return rule.DnsServer;
                        }
                    }
                    //后缀匹配
                    if (item.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parameter = item.Substring(7);
                        if (domain.EndsWith(parameter, StringComparison.OrdinalIgnoreCase))
                        {
                            return rule.DnsServer;
                        }
                    }
                    //正则匹配
                    if (item.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parameter = item.Substring(6);
                        var regex = new Regex(parameter);
                        if (regex.IsMatch(domain))
                        {
                            return rule.DnsServer;
                        }
                    }
                    //兜底匹配
                    if (item.Equals("*"))
                    {
                        return rule.DnsServer;
                    }
                }
                if (!geosites.Any())
                {
                    continue;
                }

                if (IsMatchingDomain(domain, geosites.ToArray()))
                {
                    return rule.DnsServer;
                }

                continue;
            }

            throw new Exception("未找到指定的dns服务器");
        }

    }
}
