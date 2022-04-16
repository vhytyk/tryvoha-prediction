using System;
using System.Collections.Generic;
using TL;
using System.Linq;
using System.Threading;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace TryvogaPrediction
{
    public class Program
    {
        public static string DataPath;
        public static string DataFileName;
        public static bool SendNotifications;

        public static Dictionary<string, string> RegionsPlates = new Dictionary<string, string>
        {
            { "Вінницька", "AB" },
            { "Волинська", "AC" },
            { "Дніпропетровська", "AE" },
            { "Донецька", "AH" },
            { "Житомирська", "AM" },
            { "Закарпатська", "AO" },
            { "Запорізька", "AP" },
            { "Івано-Франківська", "AT" },
            { "Київська", "AI" },
            { "Кіровоградська", "BA" },
            { "Луганська", "BB" },
            { "Львівська", "BC" },
            { "Миколаївська", "BE" },
            { "Одеська", "BH" },
            { "Полтавська", "BI" },
            { "Рівненська", "BK" },
            { "Сумська", "BM" },
            { "Тернопільська", "BO" },
            { "Харківська", "AX" },
            { "Херсонська", "BT" },
            { "Хмельницька", "BX" },
            { "Черкаська", "CA" },
            { "Чернігівська", "CB" },
            { "Чернівецька", "CE" }

        };

        public static Dictionary<string, string[]> RegionsGroups = new Dictionary<string, string[]>
        {
            { "Захід", new []{ "Закарпатська", "Львівська", "Івано-Франківська", "Чернівецька", "Тернопільська", "Хмельницька", "Рівненська", "Волинська" } },
            { "Південний схід", new []{ "Луганська", "Харківська", "Донецька", "Запорізька", "Херсонська", "Миколаївська", "Одеська", "Дніпропетровська" } },
            { "Північний центр", new []{ "Житомирська", "Київська", "Чернігівська", "Сумська", "Вінницька", "Черкаська", "Кіровоградська", "Полтавська" } },
        };



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
                    Tryvoha = regionOn.Success
                };
            }
            return null;
        }
        static void SaveToFile(Dictionary<int, TryvohaEvent> events)
        {

            File.WriteAllText(DataFileName, $"Id;EventTime;Region;OnOff{Environment.NewLine}");
            foreach (var tryvoha in events.Values.OrderBy(e => e.Id))
            {
                File.AppendAllText(DataFileName, $"{tryvoha.Id};{tryvoha.EventTime.ToString("yyyy-MM-dd HH:mm:ss")};{tryvoha.Region};{tryvoha.Tryvoha}{Environment.NewLine}");
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
                    e.Tryvoha = bool.Parse(fields[3]);
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
            SendNotifications = bool.Parse(Config["sendNotifications"] ?? "false");
            DataPath = Config["dataPath"] ?? "/tmp/tryvoha";
            DataFileName = $"{DataPath}/tryvoha.csv";

            //WTelegram.Helpers.Log = (i, s) => { };
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
            TryvohaPredictionServiceOff serviceOff = new TryvohaPredictionServiceOff();
            TryvohaPredictionServiceOn serviceOn = new TryvohaPredictionServiceOn();
            if (events.Count > 0)
            {
                serviceOn.GeneratePredictionEngines(events);
                serviceOff.GeneratePredictionEngines(events, true);
            }

            var avg = serviceOff.GetModelEvaluationsAvg();
            Console.WriteLine($"model 'OFF' - loss: {avg.Item2:0.0}, rsqr: {avg.Item1:0.00}, mae: {avg.Item3: 0.00}");
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

                Dictionary<string, bool> status = events.GroupBy(e => e.Value.Region)
                    .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Value.EventTime).Last().Value.Tryvoha);

                Dictionary<string, TryvohaPredictionRecord> predictionsOn =
                    serviceOn.ProcessPrediction(client, events, tryvogaPredictionChannel, tryvogaPredictionTest,
                        events.Except(oldEvents).ToDictionary(e => e.Key, e => e.Value));
                Tuple<double, double, double> modelEvalsOn = serviceOn.GetModelEvaluationsAvg();

                Dictionary<string, TryvohaOffPredictionRecord> predictionsOff =
                    serviceOff.ProcessPrediction(client, events, tryvogaPredictionChannel, tryvogaPredictionTest,
                        events.Except(oldEvents).ToDictionary(e => e.Key, e => e.Value));
                Tuple<double, double, double> modelEvalsOff = serviceOff.GetModelEvaluationsAvg();
                var payload = GetPayload(status, predictionsOn, modelEvalsOn, predictionsOff, modelEvalsOff);
                ShowInConsole(payload);
                SendPayload(payload);
                Thread.Sleep(10000);
            }
        }

        static void SendPayload(ResultPayload payload)
        {
            try
            {
                Console.Write($"sending payload to {Config["payload_url"]}...");
                var httpWebRequest = (HttpWebRequest)WebRequest.Create(Config["payload_url"]);
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = "POST";

                using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
                {
                    string json = JsonConvert.SerializeObject(payload);

                    streamWriter.Write(json);
                }

                httpWebRequest.GetResponse();
            }
            catch
            {
                Console.WriteLine("fail");
                return;
            }
            Console.WriteLine("done");
        }

        static ResultPayload GetPayload(Dictionary<string, bool> status,
            Dictionary<string, TryvohaPredictionRecord> predictionsOn,
            Tuple<double, double, double> modelEvalsOn,
            Dictionary<string, TryvohaOffPredictionRecord> predictionsOff,
            Tuple<double, double, double> modelEvalsOff)
        {
            ResultPayload result = new ResultPayload
            {
                Regions = new Dictionary<string, RegionStatus>(),
                ModelEvaluations = new List<string>()
            };
            foreach (var region in status.Keys)
            {
                bool isOn = status[region];
                var predictionOn = (predictionsOn.ContainsKey(region) && !isOn) ? predictionsOn[region] : null;
                var predictionOff = (predictionsOff.ContainsKey(region) && isOn) ? predictionsOff[region] : null;
                result.Regions[region] = new RegionStatus
                {
                    Status = isOn,
                    PredictedOn = predictionOn?.Prediction,
                    ProbabilityOn = predictionOn?.Probability,
                    PredictedOffMinutes = predictionOff?.Score,
                };
            }

            if (modelEvalsOn != null)
            {
                result.ModelEvaluations.Add($"model 'ON' - acc: {modelEvalsOn.Item1:0.00}, posrec: {modelEvalsOn.Item2:0.00}, f1: {modelEvalsOn.Item3:0.00}");
            }
            if (modelEvalsOff != null)
            {
                result.ModelEvaluations.Add($"model 'OFF' - loss: {modelEvalsOff.Item2:0.0}, rsqr: {modelEvalsOff.Item1:0.00}, mae: {modelEvalsOff.Item3:0.0}");
            }

            return result;
        }

        static void ShowInConsole(ResultPayload payload)
        {
            ConsoleColor standardColor = Console.ForegroundColor;

            foreach (var region in payload.Regions.Keys.OrderBy(s => s))
            {

                bool isOn = payload.Regions[region].Status;
                Console.ForegroundColor = isOn ? ConsoleColor.Red : ConsoleColor.Green;
                Console.Write($"{region}: {(isOn ? "тривога" : "немає")}");


                if (payload.Regions[region].PredictedOn.HasValue)
                {
                    var probabilty = payload.Regions[region].ProbabilityOn.Value;
                    ConsoleColor predictionColor = ConsoleColor.DarkGray;
                    if (probabilty > 0.1)
                        predictionColor = ConsoleColor.Gray;
                    if (probabilty > 0.3)
                        predictionColor = ConsoleColor.DarkYellow;
                    if (probabilty > 0.5)
                        predictionColor = ConsoleColor.Yellow;
                    if (probabilty > 0.7)
                        predictionColor = ConsoleColor.Red;
                    Console.ForegroundColor = predictionColor;
                    Console.Write($" ({payload.Regions[region].PredictedOn}, {probabilty * 100:0.0}%)");
                }

                if (payload.Regions[region].PredictedOffMinutes.HasValue)
                {
                    Console.Write($" ({payload.Regions[region].PredictedOffMinutes:0} mins)");
                }
                Console.WriteLine();
            }

            Console.ForegroundColor = standardColor;
            payload.ModelEvaluations.ForEach(Console.WriteLine);
        }

    }
}