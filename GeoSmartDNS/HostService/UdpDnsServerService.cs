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
        private readonly IDnsService _dnsService;

        public UdpDnsServerService(
            ILogger<UdpDnsServerService> logger,
            IGeoSiteService geoSiteService,
            IDnsService dnsService)
        {
            _logger = logger;
            _geoSiteService = geoSiteService;
            _dnsService = dnsService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            DnsServer dnsServer = new DnsServer();

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
                        byte[] responseData = await _dnsService.HandleDnsRequest(result.Buffer);
                        if(responseData == null)
                        {
                            return;
                        }

                        await udpServer.SendAsync(responseData, responseData.Length, remoteEP);
                    });
                }

                catch (SocketException sc)
                {

                }
                catch (OperationCanceledException)
                {
                   // GC.Collect();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"{ex.Message}\n{ex.StackTrace}");
                }
            }

            udpServer.Close();
        }
    }
}
