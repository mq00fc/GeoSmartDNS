using System.Net.Sockets;
using System.Net;
using TechnitiumLibrary.Net.Dns;
using GeoSmartDNS.Implement;
using GeoSmartDNS.Models;
using System.Diagnostics;
namespace GeoSmartDNS.HostService
{
    public class UdpDnsServerService : BackgroundService
    {
        private readonly ILogger<UdpDnsServerService> _logger;
        private readonly IGeoSiteService _geoSiteService;

        public UdpDnsServerService(
            ILogger<UdpDnsServerService> logger,
            IGeoSiteService geoSiteService)
        {
            _logger = logger;
            _geoSiteService = geoSiteService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using UdpClient udpServer = new UdpClient(5383);
            udpServer.EnableBroadcast = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udpServer.ReceiveAsync(stoppingToken);
                    await Task.Run(async () =>
                    {
                        IPEndPoint remoteEP = result.RemoteEndPoint;
                        byte[] requestData = result.Buffer;
                        byte[] responseData = await HandleDnsRequest(requestData);
                        await udpServer.SendAsync(responseData, responseData.Length, remoteEP);
                    });
                }

                catch (SocketException sc)
                {

                }
                catch(OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    _logger.LogError($"{ex.Message}\n{ex.StackTrace}");
                }
            }

            udpServer.Close();
        }

        private async Task<byte[]> HandleDnsRequest(byte[] requestData)
        {
            DnsDatagram dnsRequest;
            using (MemoryStream ms = new MemoryStream())
            {
                await ms.WriteAsync(requestData, 0, requestData.Length);
                ms.Position = 0; // 读取前将位置重置为0
                dnsRequest = DnsDatagram.ReadFrom(ms);
            }


            var domain = dnsRequest.Question[0].Name;

            var dnsClient = _geoSiteService.GetDnsClient(domain);

            Stopwatch sw = Stopwatch.StartNew();
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
