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

        static string ConfigForTelegramClient(string what)
        {
            switch (what)
            {
                case "api_id": return Config.TgApiId;
                case "api_hash": return Config.TgApiHash;
                case "phone_number": return Config.TgPhoneNumber;
                case "verification_code": Console.Write("Code: "); return Console.ReadLine();
                case "password": return Config.TgPassword;
                default: return null;
            }
        }
        static bool _init = true;
        public static void Main(string[] args)
        {
            Config.ReadConfig("appsettings.json");

            //WTelegram.Helpers.Log = (i, s) => { };
            Console.OutputEncoding = Encoding.UTF8;

            using var client = new WTelegram.Client(ConfigForTelegramClient);
            var my = client.LoginUserIfNeeded().Result;
            Messages_Chats allChats = client.Messages_GetAllChats().Result;
            Channel tryvogaChannel = (Channel)allChats.chats[1766138888];
            Channel tryvogaPredictionChannel = (Channel)allChats.chats[1766772788];
            Channel tryvogaPredictionTest = (Channel)allChats.chats[1660739731];

            if (!Directory.Exists(Config.DataPath))
            {
                Directory.CreateDirectory(Config.DataPath);
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
                    .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Value.EventTime).Last().Value.OnOff);

                Dictionary<string, TryvohaPredictionRecord> predictionsOn =
                    serviceOn.ProcessPrediction(client, events, tryvogaPredictionChannel, tryvogaPredictionTest,
                        events.Except(oldEvents).ToDictionary(e => e.Key, e => e.Value));
                Tuple<double, double, double> modelEvalsOn = serviceOn.GetModelEvaluationsAvg();

                Dictionary<string, OffTryvohaPredictionRecord> predictionsOff =
                    serviceOff.ProcessPrediction(client, events, tryvogaPredictionChannel, tryvogaPredictionTest,
                        events.Except(oldEvents).ToDictionary(e => e.Key, e => e.Value));
                Tuple<double, double> modelEvalsOff = serviceOff.GetModelEvaluationsAvg();
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
                if (string.IsNullOrEmpty(Config.PayloadUrl))
                {
                    return;
                }
                Console.Write($"sending payload to {Config.PayloadUrl}...");
                var httpWebRequest = (HttpWebRequest) WebRequest.Create(Config.PayloadUrl);
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
            Dictionary<string, OffTryvohaPredictionRecord> predictionsOff,
            Tuple<double, double> modelEvalsOff)
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
                result.ModelEvaluations.Add($"model 'OFF' - loss: {modelEvalsOff.Item2:0.0}, rsqr: {modelEvalsOff.Item1:0.00}");
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