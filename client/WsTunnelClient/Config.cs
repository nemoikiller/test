using System;
namespace WsTunnelClient
{
    public class AppConfig
    {
        public string ServerUrl { get; set; }
        public string ClientId { get; set; }
        public string MasterKey { get; set; }
        public string Info { get; set; }

        // Полностью исключаем config.json: возвращаем жёстко заданные параметры
        public static AppConfig Load()
        {
            return new AppConfig
            {
                ServerUrl = "ws://185.39.30.19:8080/ws",
                ClientId = null,
                MasterKey = "",
                Info = ""
            };
        }
    }
}
