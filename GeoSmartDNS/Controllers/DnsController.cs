using System.Diagnostics;
using GeoSmartDNS.Implement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TechnitiumLibrary.Net.Dns;

namespace GeoSmartDNS.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DnsController : ControllerBase
    {
        private readonly ILogger<DnsController> _logger;
        private readonly IGeoSiteService _geoSiteService;
        public DnsController(
            ILogger<DnsController> logger,
            IGeoSiteService geoSiteService)
        {
            _logger = logger;
            _geoSiteService = geoSiteService;
        }

        [HttpGet("/dns-query")]
        public async Task<IActionResult> GetDns(string dns)
        {
            var request = HttpContext.Request;
            string requestAccept = request.Headers["Accept"];

            bool acceptsDoH = false;
            if (string.IsNullOrEmpty(requestAccept))
            {
                acceptsDoH = true;
            }
            else
            {
                foreach (string mediaType in requestAccept.Split(','))
                {
                    if (mediaType.Equals("application/dns-message", StringComparison.OrdinalIgnoreCase))
                    {
                        acceptsDoH = true;
                        break;
                    }
                }
            }

            if (!acceptsDoH || string.IsNullOrEmpty(dns))
            {
                return BadRequest();
            }

            //convert from base64url to base64
            dns = dns.Replace('-', '+');
            dns = dns.Replace('_', '/');
            int x = dns.Length % 4;
            if (x > 0)
                dns = dns.PadRight(dns.Length - x + 4, '=');

            DnsDatagram dnsRequest;
            using (MemoryStream mS = new MemoryStream(Convert.FromBase64String(dns)))
            {
                dnsRequest = DnsDatagram.ReadFrom(mS);
            }

            var domain = dnsRequest.Question[0].Name;

            var dnsClient = _geoSiteService.GetDnsClient(domain);

            DnsDatagram dnsResponse = await dnsClient.ResolveAsync(dnsRequest);

            using (MemoryStream ms = new MemoryStream())
            {
                dnsResponse.WriteTo(ms);
                ms.Position = 0;
                HttpContext.Response.ContentType = "application/dns-message";
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.Body.WriteAsync(ms.ToArray());
            }

            return new EmptyResult();
        }



        [HttpPost("/dns-query")]
        public async Task<IActionResult> PostDns()
        {
            var request = HttpContext.Request;
            if (!string.Equals(request.Headers["Content-Type"], "application/dns-message", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(415);
            }

            DnsDatagram dnsRequest;
            using (MemoryStream ms = new MemoryStream())
            {
                await request.Body.CopyToAsync(ms);
                ms.Position = 0;
                dnsRequest = DnsDatagram.ReadFrom(ms);
            }

            var domain = dnsRequest.Question[0].Name;
            var dnsClient = _geoSiteService.GetDnsClient(domain);

            DnsDatagram dnsResponse = await dnsClient.ResolveAsync(dnsRequest);

            using (MemoryStream ms = new MemoryStream())
            {
                dnsResponse.WriteTo(ms);
                ms.Position = 0;
                HttpContext.Response.ContentType = "application/dns-message";
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.Body.WriteAsync(ms.ToArray());
            }

            return new EmptyResult();
        }
    }
}
