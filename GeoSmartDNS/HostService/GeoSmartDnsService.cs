using GeoSmartDNS.Implement;
using Microsoft.Extensions.Options;

namespace GeoSmartDNS.HostService
{
    public class GeoSmartDnsService : BackgroundService
    {
        private readonly ILogger<GeoSmartDnsService> _logger;
        private readonly IGeoSiteService _geoSiteService;

        public GeoSmartDnsService(
            ILogger<GeoSmartDnsService> logger,
            IGeoSiteService geoSiteService)
        {
            _logger = logger;
            _geoSiteService = geoSiteService;
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var geoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "geosite.dat");
            await _geoSiteService.LoadGeoSiteFile(geoPath);
        }
    }
}
