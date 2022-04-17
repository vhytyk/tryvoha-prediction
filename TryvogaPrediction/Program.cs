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
        public static string PayloadUrl;

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
            PayloadUrl = Config["payload_url"];

            //WTelegram.Helpers.Log = (i, s) => { };
            Console.OutputEncoding = Encoding.UTF8;

            using var client = new WTelegram.Client(ConfigForTelegramClient);
            var my = client.LoginUserIfNeeded().Result;
            Messages_Chats allChats = client.Messages_GetAllChats().Result;
            Channel tryvogaChannel = (Channel)allChats.chats[1766138888];
            Channel tryvogaPredictionChannel = (Channel)allChats.chats[1766772788];
            Channel tryvogaPredictionTest = (Channel)allChats.chats[1660739731];

            #region show test
            //SendNotificationsToTelegram(new ResultPayload
            //{
            //    Regions = new Dictionary<string, RegionStatus>
            //{
            //    {"Закарпатська", new RegionStatus{ Status = true, PredictedOffMinutes = 15, Minutes=10} },
            //    {"Львівська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.1, Minutes=30} },
            //    {"Івано-Франківська", new RegionStatus{ Status = false, PredictedOn = true, ProbabilityOn = 0.6, Minutes=20} },
            //}
            //}, tryvogaPredictionTest, client);
            //Thread.Sleep(5000);
            //SendNotificationsToTelegram(new ResultPayload
            //{
            //    Regions = new Dictionary<string, RegionStatus>
            //{
            //    {"Закарпатська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.1, Minutes=5} },
            //    {"Львівська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.1, Minutes=30} },
            //    {"Івано-Франківська", new RegionStatus{ Status = false, PredictedOn = true, ProbabilityOn = 0.6, Minutes=20} },
            //}
            //}, tryvogaPredictionTest, client);
            //Thread.Sleep(5000);
            //SendNotificationsToTelegram(new ResultPayload
            //{
            //    Regions = new Dictionary<string, RegionStatus>
            //{
            //    {"Закарпатська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.5, Minutes=5} },
            //    {"Львівська", new RegionStatus{ Status = false, PredictedOn = true, ProbabilityOn = 0.1, Minutes=2}  },
            //    {"Івано-Франківська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.1, Minutes=10} } },
            //}, tryvogaPredictionTest, client);
            //Thread.Sleep(5000);
            //SendNotificationsToTelegram(new ResultPayload
            //{
            //    Regions = new Dictionary<string, RegionStatus>
            //{
            //    {"Закарпатська", new RegionStatus{ Status = false, PredictedOn = true, ProbabilityOn = 0.5, Minutes=2} },
            //    {"Львівська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.2, Minutes=3}  },
            //    {"Івано-Франківська", new RegionStatus{ Status = false, PredictedOn = false, ProbabilityOn = 0.1, Minutes=4} } },
            //}, tryvogaPredictionTest, client);
            //return;
            #endregion

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
                serviceOff.GeneratePredictionEngines(events);
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

                Dictionary<string, TryvohaEvent> status = events.GroupBy(e => e.Value.Region)
                    .ToDictionary(g => g.Key, g =>  g.OrderBy(e => e.Value.EventTime).Last().Value);

                Dictionary<string, TryvohaPredictionRecord> predictionsOn = serviceOn.ProcessPrediction(events);
                Tuple<double, double, double> modelEvalsOn = serviceOn.GetModelEvaluationsAvg();

                Dictionary<string, TryvohaOffPredictionRecord> predictionsOff = serviceOff.ProcessPrediction(events);
                Tuple<double, double, double> modelEvalsOff = serviceOff.GetModelEvaluationsAvg();

                var payload = GetPayload(status, predictionsOn, modelEvalsOn, predictionsOff, modelEvalsOff);

                SendPayload(payload);
                SendNotificationsToTelegram(payload, tryvogaPredictionChannel, client);
                ShowInConsole(payload);
                Thread.Sleep(10000);
            }
        }

        static void SendPayload(ResultPayload payload)
        {
            try
            {
                if (string.IsNullOrEmpty(PayloadUrl))
                {
                    return;
                }

                Console.Write($"sending payload to {PayloadUrl}...");
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

        static Dictionary<string, RegionStatus> OldStatuses = new Dictionary<string, RegionStatus>();
        static DateTime LastShowOff = DateTime.MinValue;
        static void SendNotificationsToTelegram(ResultPayload payload, Channel tgChannel, WTelegram.Client client)
        {
            string[] notificationRegions = { "Закарпатська", "Львівська", "Івано-Франківська" };
            if (!SendNotifications)
            {
                return;
            }

            bool needToShow = false;
            int vidbijMins = 15;
            Dictionary<string, RegionStatus> newStatuses = payload.Regions.Where(g => notificationRegions.Contains(g.Key)).ToDictionary(g => g.Key, g => g.Value);
            if((OldStatuses == null && !newStatuses.Any(s => s.Value.Status || (s.Value.PredictedOn.HasValue && s.Value.PredictedOn.Value)))
                || (newStatuses.All(s => s.Value.Minutes <= vidbijMins) && OldStatuses != null && OldStatuses.All(s => s.Value.Minutes <= vidbijMins)))
            {
                OldStatuses = new Dictionary<string, RegionStatus>(newStatuses);
                return;
            }
            foreach (var region in newStatuses)
            {
                var newStatus = region.Value;
                var oldStatus = OldStatuses.ContainsKey(region.Key) ? OldStatuses[region.Key] : null;
                double showOffMinutes = (DateTime.UtcNow - LastShowOff).TotalMinutes;
                if (oldStatus == null && !newStatus.Status && newStatus.PredictedOn.HasValue && newStatus.PredictedOn.Value)
                {
                    needToShow = true;
                }
                if (oldStatus != null && !newStatus.Status && oldStatus.Status)
                {
                    needToShow = true;
                }
                if (oldStatus != null && !newStatus.Status && oldStatus.PredictedOn != newStatus.PredictedOn)
                {
                    needToShow = true;
                }
                if (oldStatus != null && !newStatus.Status && oldStatus.PredictedOn == newStatus.PredictedOn && oldStatus.ProbabilityOn != newStatus.ProbabilityOn)
                {
                    needToShow = true;
                }
                if (oldStatus == null && newStatus.Status && showOffMinutes > 5)
                {
                    needToShow = true;
                    LastShowOff = DateTime.UtcNow;
                }
                if (oldStatus != null && newStatus.Status && newStatus.PredictedOffMinutes != oldStatus.PredictedOffMinutes && showOffMinutes > 5)
                {
                    needToShow = true;
                    LastShowOff = DateTime.UtcNow;
                }
                if (oldStatus != null && newStatus.Status && !oldStatus.Status)
                {
                    needToShow = true;
                    LastShowOff = DateTime.UtcNow;
                }
            }
            if (needToShow)
            {
                StringBuilder messageBuilder = new StringBuilder();
                messageBuilder.AppendLine($"*{DateTime.UtcNow.AddHours(3).ToString("HH:mm")} Оновлення:*");
                foreach (var status in newStatuses)
                {
                    string statusText = status.Value.PredictedOn.HasValue
                            ? (status.Value.PredictedOn.Value && status.Value.Minutes > vidbijMins ? "можливо:" : (status.Value.Minutes <= vidbijMins ? "відбій." : "немає:"))
                            : (status.Value.PredictedOffMinutes.HasValue ? "тривога:" : (status.Value.Status ? "тривога." : "немає."));
                    string statusValue = status.Value.PredictedOn.HasValue
                            ? (status.Value.Minutes > vidbijMins ? $"{status.Value.ProbabilityOn * 100:0}%" : string.Empty)
                            : (status.Value.PredictedOffMinutes.HasValue ? $"~{Math.Abs(status.Value.PredictedOffMinutes.Value):0}хв" : "");
                    string statusSmile = status.Value.PredictedOn.HasValue
                            ? (status.Value.PredictedOn.Value && status.Value.Minutes > vidbijMins ? "⚠️" : "🍀")
                            : (status.Value.PredictedOffMinutes.HasValue ? "🔴" : (status.Value.Status ? "🔴" : "🍀"));
                    messageBuilder.AppendLine($"{statusSmile} {status.Key} - {statusText} {statusValue}");
                }
                var message = messageBuilder.ToString();
                var entities = client.MarkdownToEntities(ref message);
                client.SendMessageAsync(new InputChannel(tgChannel.id, tgChannel.access_hash), message, entities: entities);
                Console.WriteLine(messageBuilder);
            }
            OldStatuses = new Dictionary<string, RegionStatus>(newStatuses);
        }

        static ResultPayload GetPayload(Dictionary<string, TryvohaEvent> status,
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
                bool isOn = status[region].Tryvoha;
                var predictionOn = (predictionsOn.ContainsKey(region) && !isOn) ? predictionsOn[region] : null;
                var predictionOff = (predictionsOff.ContainsKey(region) && isOn) ? predictionsOff[region] : null;
                result.Regions[region] = new RegionStatus
                {
                    Minutes = (int)(DateTime.UtcNow - status[region].EventTime).TotalMinutes,
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