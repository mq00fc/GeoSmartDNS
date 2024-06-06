using TechnitiumLibrary.Net.Dns;

namespace GeoSmartDNS.Implement
{
    public interface IGeoSiteService
    {
        Task LoadGeoSiteFile(string path);

        Task LoadGeoSiteFile(byte[] bytes);

        bool IsMatchingDomain(string domain, params string[] countryCodes);

        DnsClient GetDnsClient(string domain);
    }
}
