using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace TryvogaPrediction
{
    public class Config
    {
        public static string DataPath;
        public static string DataFileName;
        public static bool SendNotifications;
        public static string PayloadUrl;
        public static string TgApiId;
        public static string TgApiHash;
        public static string TgPassword;
        public static string TgPhoneNumber;


        static IConfiguration FileConfig;
        public static void ReadConfig(string fileName)
        {
            FileConfig = new ConfigurationBuilder()
               .AddJsonFile(fileName, optional: true)
               .Build();
            PayloadUrl = FileConfig["payload_url"];
            SendNotifications = bool.Parse(FileConfig["sendNotifications"] ?? "false");
            DataPath = FileConfig["dataPath"] ?? "/tmp/tryvoha";
            DataFileName = $"{DataPath}/tryvoha.csv";
            TgApiId = FileConfig["api_id"];
            TgApiHash = FileConfig["api_hash"];
            TgPhoneNumber = FileConfig["phone_number"];
            TgPassword = FileConfig["password"];
        }
    }
}
