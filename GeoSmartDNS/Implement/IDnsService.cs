namespace GeoSmartDNS.Implement
{
    public interface IDnsService
    {
        void Installation();
        Task<byte[]> HandleDnsRequest(byte[] requestData);
    }
}
