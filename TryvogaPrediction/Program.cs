using System;
using System.Collections.Generic;
using TL;
using System.Linq;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace TryvogaPrediction
{
    public class Program
    {
        public static string DataPath = "/tmp/tryvoha";
        public static string DataFileName = $"{DataPath}/tryvoha.csv";

        public static IConfiguration Config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

        static string ConfigForTelegramClient(string what)
        {
            switch (what)
            {
                case "api_id": return Config["api_id"];
                case "api_hash": return Config["api_hash"];
                case "phone_number": return Config["phone_number"];
                case "verification_code": Console.Write("Code: "); return Console.ReadLine();
                case "password": return Config["password"];     // if user has enabled 2FA
                default: return null;                  // let WTelegramClient decide the default config
            }
        }
        static bool _init = true;
        static void GetEvents(WTelegram.Client client, Channel tryvoga, Dictionary<int, TryvohaEvent> events)
        {
            try
            {
                DateTime minDateTime = _init && events.Count > 0 ? events.Values.Min(v => v.EventTime) : DateTime.UtcNow;

                var msg = client.Messages_GetHistory(new InputChannel(tryvoga.id, tryvoga.access_hash), offset_date: minDateTime).Result;
                foreach (var m in msg.Messages)
                {
                    Message message = m as Message;
                    if (m == null)
                    {
                        continue;
                    }
                    var tryvoha = GetTryvohaFromMessage(message);
                    if (tryvoha != null
                        && !events.ContainsKey(message.id))
                    {
                        events.Add(message.id, tryvoha);
                    }
                }
            }
            catch { }

        }

        static TryvohaEvent GetTryvohaFromMessage(Message message)
        {
            Match regionOn = Regex.Match(message.message, @"Повітряна тривога в\s+(.*)\s+област.*\n");
            Match regionOff = Regex.Match(message.message, @"Відбій тривоги в\s+(.*)\s+област.*\n");
            if (regionOn.Success || regionOff.Success)
            {
                return new TryvohaEvent
                {
                    Id = message.id,
                    EventTime = message.date,
                    Region = (regionOn.Success
                    ? regionOn.Groups[1].Value
                    : regionOff.Groups[1].Value).Replace(';', '.').Trim('.'),
                    OnOff = regionOn.Success
                };
            }
            return null;
        }
        static void SaveToFile(Dictionary<int, TryvohaEvent> events)
        {

            File.WriteAllText(DataFileName, $"Id;EventTime;Region;OnOff{Environment.NewLine}");
            foreach (var tryvoha in events.Values.OrderBy(e => e.Id))
            {
                File.AppendAllText(DataFileName, $"{tryvoha.Id};{tryvoha.EventTime.ToString("yyyy-MM-dd HH:mm:ss")};{tryvoha.Region};{tryvoha.OnOff}{Environment.NewLine}");
            }
        }

        public static Dictionary<int, TryvohaEvent> LoadFromFile()
        {
            Dictionary<int, TryvohaEvent> result = new Dictionary<int, TryvohaEvent>();
            if (!File.Exists(DataFileName))
            {
                return result;
            }
            using (TextFieldParser parser = new TextFieldParser(DataFileName))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(";");
                while (!parser.EndOfData)
                {
                    //Process row
                    string[] fields = parser.ReadFields();
                    if (fields[0] == "Id")
                    {
                        continue;
                    }
                    TryvohaEvent e = new TryvohaEvent();
                    e.Id = int.Parse(fields[0]);
                    e.EventTime = DateTime.Parse(fields[1]);
                    e.Region = fields[2];
                    e.OnOff = bool.Parse(fields[3]);
                    result.Add(e.Id, e);
                }
            }
            return result;
        }

        static void FillInEvents(WTelegram.Client client, Channel tryvoga, Dictionary<int, TryvohaEvent> events, IEnumerable<int> loadedKeys = null)
        {
            Console.WriteLine($"{Environment.NewLine}{DateTime.Now}: getting new events...");
            int msgCount = -1;
            while (msgCount < events.Count && (loadedKeys == null || !events.Keys.Intersect(loadedKeys).Any()))
            {
                Thread.Sleep(1000);
                msgCount = events.Count;
                GetEvents(client, tryvoga, events);
                Console.WriteLine($"events added {events.Count - msgCount}, total count: {events.Count}");
            }
            SaveToFile(events);
        }
        
        public static void Main(string[] args)
        {
           
            WTelegram.Helpers.Log = (i, s) => { };
            Console.OutputEncoding = Encoding.UTF8;

            using var client = new WTelegram.Client(ConfigForTelegramClient);
            var my = client.LoginUserIfNeeded().Result;
            Messages_Chats allChats = client.Messages_GetAllChats().Result;
            Channel tryvogaChannel = (Channel)allChats.chats[1766138888];
            Channel tryvogaPredictionChannel = (Channel)allChats.chats[1766772788];
            Channel tryvogaPredictionTest = (Channel)allChats.chats[1660739731];

            if (!Directory.Exists(DataPath))
            {
                Directory.CreateDirectory(DataPath);
            }

            Dictionary<int, TryvohaEvent> events = LoadFromFile();
            TryvohaPredictionServiceOn serviceOn = new TryvohaPredictionServiceOn();
            if (events.Count > 0)
            {
                serviceOn.GeneratePredictionEngines(events);
            }

            Dictionary<int, TryvohaEvent> initialEvents = new Dictionary<int, TryvohaEvent>();

            Console.WriteLine($"loaded from db: {events.Count}. Reading new events.");
            FillInEvents(client, tryvogaChannel, initialEvents, events.Keys);

            foreach (var e in initialEvents)
            {
                if (!events.ContainsKey(e.Key))
                {
                    events.Add(e.Key, e.Value);
                }
            }

            _init = false;
            while (true)
            {
                Dictionary<int, TryvohaEvent> oldEvents = new Dictionary<int, TryvohaEvent>(events);
                FillInEvents(client, tryvogaChannel, events);
                serviceOn.ShowPredictionMessage(client, events, tryvogaPredictionChannel, tryvogaPredictionTest, 
                    events.Except(oldEvents).ToDictionary(e => e.Key, e => e.Value));

                Thread.Sleep(10000);
            }
        }


     
    }
}