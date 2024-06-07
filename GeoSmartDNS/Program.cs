using GeoSmartDNS.HostService;
using GeoSmartDNS.Implement;
using GeoSmartDNS.Models;

namespace GeoSmartDNS
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.None);

            //���������˿�
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(8125);
            });
            builder.Services.AddControllers();

            builder.Services.AddMemoryCache();
            var configurationBuilder = new ConfigurationBuilder()
                  .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            var configurationRoot = configurationBuilder.Build();
            builder.Services.Configure<DnsConfig>(configurationRoot.GetSection("SmartDnsConfig"));
            builder.Services.AddSingleton<IConfigurationRoot>(configurationRoot);

            builder.Services.AddSingleton<IDnsService, DnsService>();
            builder.Services.AddSingleton<IGeoSiteService, GeoSiteService>();


            //ϵͳ����Ԥ��
            builder.Services.AddHostedService<GeoSmartDnsService>();
            //udp:53��׼dns������
            builder.Services.AddHostedService<UdpDnsServerService>();
            var app = builder.Build();
            app.UseAuthorization();
            app.MapControllers();
            app.Run();
        }
    }
}
