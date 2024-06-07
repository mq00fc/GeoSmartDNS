using System.Diagnostics;
using System.Net;
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
        private readonly IDnsService _dnsService;
        public DnsController(
            ILogger<DnsController> logger,
            IGeoSiteService geoSiteService,
            IDnsService dnsService)
        {
            _logger = logger;
            _geoSiteService = geoSiteService;
            _dnsService = dnsService;
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

            var buffer = await _dnsService.HandleDnsRequest(Convert.FromBase64String(dns));
            if(buffer == null)
            {
                return BadRequest();
            }

            HttpContext.Response.ContentType = "application/dns-message";
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.Body.WriteAsync(buffer);

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


          
            using (MemoryStream ms = new MemoryStream())
            {
                await request.Body.CopyToAsync(ms);
                ms.Position = 0;
                HttpContext.Response.ContentType = "application/dns-message";
                HttpContext.Response.StatusCode = 200;
                var buffer = await _dnsService.HandleDnsRequest(ms.ToArray());
                if (buffer == null)
                {
                    return BadRequest();
                }
                await HttpContext.Response.Body.WriteAsync(buffer);
            }

      
            return new EmptyResult();
        }
    }
}
