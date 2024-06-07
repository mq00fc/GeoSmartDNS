using System.Diagnostics;
using GeoSmartDNS.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace GeoSmartDNS.Implement
{
    public class DnsService : IDnsService
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<DnsService> _logger;
        private readonly IOptions<DnsConfig> _dnsConfig;
        private readonly IGeoSiteService _geoSiteService;

        //DnsServer
        private Dictionary<string, NameServerAddress[]> _dnsServerDic = new Dictionary<string, NameServerAddress[]>(StringComparer.InvariantCultureIgnoreCase);
        //GeoSite
        private Dictionary<string, GeoSite> _geoDictionary = new Dictionary<string, GeoSite>(StringComparer.InvariantCultureIgnoreCase);
        public DnsService(IMemoryCache memoryCache,
                              ILogger<DnsService> logger,
                              IOptions<DnsConfig> dnsConfig,
                              IGeoSiteService geoSiteService)
        {
            _memoryCache = memoryCache;
            _logger = logger;
            _dnsConfig = dnsConfig;
            _geoSiteService = geoSiteService;
        }

        public void Installation()
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

        public async Task<byte[]> HandleDnsRequest(byte[] requestData)
        {
            DnsDatagram dnsRequest;
            using (MemoryStream ms = new MemoryStream())
            {
                await ms.WriteAsync(requestData, 0, requestData.Length);
                ms.Position = 0; // 读取前将位置重置为0
                dnsRequest = DnsDatagram.ReadFrom(ms);
            }


            var domain = dnsRequest.Question[0].Name;

            Stopwatch sw = Stopwatch.StartNew();
            var dnsName = _geoSiteService.GetDnsNameServer(domain);
            _logger.LogInformation($"域名:{domain}\t命中规则:{dnsName}，耗时:{sw.ElapsedMilliseconds}");
            DnsClient? dnsClient = _memoryCache.Get<DnsClient>(dnsName);

            if (dnsClient == null)
            {
                var nameServers = _dnsServerDic[dnsName];
                if (nameServers == null)
                {
                    throw new Exception($"DnsName:{dnsName} Undefined!");
                }
                var config = _dnsConfig.Value.DnsServers.FirstOrDefault(x => x.Name == dnsName);
                dnsClient = new DnsClient(nameServers);
                dnsClient.Concurrency = nameServers.Length;
                dnsClient.Retries = 5;
                dnsClient.Timeout = 2000;
                if (!string.IsNullOrEmpty(config.Proxy))
                {
                    var proxyServer = _dnsConfig.Value.ProxyServers.FirstOrDefault(x => x.Name == config.Proxy);
                    if (proxyServer != null)
                    {
                        dnsClient.Proxy = NetProxy.CreateSocksProxy(proxyServer.ProxyAddress, proxyServer.ProxyPort);
                    }
                }
                _memoryCache.Set(dnsName,dnsClient);
            }

            sw.Reset();
            DnsDatagram dnsResponse = await dnsClient.ResolveAsync(dnsRequest);
            _logger.LogInformation($"域名:{domain}，解析耗时:{sw.ElapsedMilliseconds}ms");
            using (MemoryStream ms = new MemoryStream())
            {
                dnsResponse.WriteTo(ms);
                return ms.ToArray();
            }
        }
    }
}
