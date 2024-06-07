namespace GeoSmartDNS.Implement
{
    public interface IDnsService
    {
        Task<byte[]> HandleDnsRequest(byte[] requestData);
    }
}
