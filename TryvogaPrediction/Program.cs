using System;
using System.Collections.Generic;
using TL;
using System.Linq;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Microsoft.ML;
using Microsoft.ML.Data;
using static Microsoft.ML.DataOperationsCatalog;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace TryvogaPrediction
{
    class Program
    {
        static string path = "/tmp/tryvoha";
        static string fileName = $"{path}/tryvoha.csv";
        static IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

        static string ConfigForTelegramClient(string what)
        {
            switch (what)
            {
                case "api_id": return config["api_id"];
                case "api_hash": return config["api_hash"];
                case "phone_number": return config["phone_number"];
                case "verification_code": Console.Write("Code: "); return Console.ReadLine();
                case "password": return config["password"];     // if user has enabled 2FA
                default: return null;                  // let WTelegramClient decide the default config
            }
        }
        static bool init = true;
        static void GetEvents(WTelegram.Client client, Channel tryvoga, Dictionary<int, TryvohaEvent> events)
        {
            try
            {
                DateTime minDateTime = init && events.Count > 0 ? events.Values.Min(v => v.EventTime) : DateTime.UtcNow;

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

            File.WriteAllText(fileName, $"Id;EventTime;Region;OnOff{Environment.NewLine}");
            foreach (var tryvoha in events.Values.OrderBy(e => e.Id))
            {
                File.AppendAllText(fileName, $"{tryvoha.Id};{tryvoha.EventTime.ToString("yyyy-MM-dd HH:mm:ss")};{tryvoha.Region};{tryvoha.OnOff}{Environment.NewLine}");
            }
        }

        public static Dictionary<int, TryvohaEvent> LoadFromFile()
        {
            Dictionary<int, TryvohaEvent> result = new Dictionary<int, TryvohaEvent>();
            if (!File.Exists(fileName))
            {
                return result;
            }
            using (TextFieldParser parser = new TextFieldParser(fileName))
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

        public static ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {

            var estimator = mlContext.Transforms.Text.FeaturizeText(outputColumnName: "Features", inputColumnName: nameof(TryvohaTrainingRecord.RegionsOn))
             .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(labelColumnName: "Label", featureColumnName: "Features"));

            var model = estimator.Fit(splitTrainSet);

            return model;
        }

        public static CalibratedBinaryClassificationMetrics Evaluate(MLContext mlContext, ITransformer model, IDataView splitTestSet)
        {
            IDataView predictions = model.Transform(splitTestSet);

            CalibratedBinaryClassificationMetrics metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");

            return metrics;
        }
        static Dictionary<string, string> regionsSmall = new Dictionary<string, string>
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
        static void GenerateData(string region)
        {
            Console.Write($"Generating train set for {region}...");
            Dictionary<int, TryvohaEvent> events = LoadFromFile();
            File.WriteAllText($"{path}/{region}.csv", $"RegionsOn;Min10{Environment.NewLine}");

            foreach (var ev in events.Values.Where(e => e.Region != region))
            {
                var previousEvents = events.Values.Where(e => e.Id <= ev.Id);
                var grouped = previousEvents.GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
                }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
                var r = new TryvohaTrainingRecord
                {
                    RegionsOn = string.Join(" ", grouped.Select(g => regionsSmall[g.Region])),
                    Min10 = events.Values.Where(e => e.EventTime > ev.EventTime && e.EventTime <= ev.EventTime.AddMinutes(20) && e.Region == region && e.OnOff).Any()
                };
                File.AppendAllText($"{path}/{region}.csv", $"{r.RegionsOn};{(r.Min10 ? 1 : 0)}{Environment.NewLine}");
            }
            Console.WriteLine("done");
        }
        static List<double> posAvg = new List<double>();
        static PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord> CreatePredictionEngine(string region, bool regenerate = false)
        {
            Console.Write($"Creating prediction engine for {region}...");
            MLContext mlContext = new MLContext();
            IDataView dataView = mlContext.Data.LoadFromTextFile<TryvohaTrainingRecord>($"{path}/{region}.csv", hasHeader: true, separatorChar: ';');
            TrainTestData splitDataView = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);


            var model = File.Exists($"{path}/{region}.zip") && !regenerate
                ? mlContext.Model.Load($"{path}/{region}.zip", out _)
                : BuildAndTrainModel(mlContext, splitDataView.TrainSet);
            if (!File.Exists($"{path}/{region}.zip") || regenerate)
            {
                mlContext.Model.Save(model, dataView.Schema, $"{path}/{region}.zip");
            }
            var eval = Evaluate(mlContext, model, splitDataView.TestSet);
            Console.WriteLine($" acc: {eval.Accuracy:0.00}, posrec: {eval.PositiveRecall:0.00}, f1: {eval.F1Score:0.00}");
            posAvg.Add(eval.PositiveRecall);
            return mlContext.Model.CreatePredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>(model);
        }

        static Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>> GetPredictionEngines(Dictionary<int, TryvohaEvent> events, bool regenerate = false)
        {
            var regions = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key)
                .Where(e => e != "Луганська" && e != "Донецька" && e != "Херсонська").OrderBy(e => e);
            Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>> result
                = new Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>>();
            foreach (string region in regions)
            {
                GenerateData(region);
                result[region] = CreatePredictionEngine(region, regenerate);
            }
            return result;
        }

        static void ShowPredictionMessage(WTelegram.Client client,
            Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>> predictionEngines,
            Dictionary<int, TryvohaEvent> events,
            Channel tryvogaPrediction,
            Channel tryvogaPredictionTest,
            bool newEvents)
        {
            string[] notificationRegions = new string[] { "Закарпатська", "Львівська", "Івано-Франківська" };
            var groupedForPrediction = events.Values.GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
            }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
            TryvohaTrainingRecord sampleStatement = new TryvohaTrainingRecord
            {
                RegionsOn = string.Join(',', groupedForPrediction.Select(g => regionsSmall[g.Region]))
            };
            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key).OrderBy(e => e);
            ConsoleColor color = Console.ForegroundColor;
            foreach (var group in grouped)
            {
                var last = events.Values.OrderBy(e => e.EventTime).Last(e => e.Region == group);
                Console.ForegroundColor = last.OnOff ? ConsoleColor.Red : ConsoleColor.Green;
                Console.Write($"{group}: {(last.OnOff ? "тривога" : "немає")}");
                if (predictionEngines.ContainsKey(group) && !last.OnOff)
                {
                    var predictionResult = predictionEngines[group].Predict(sampleStatement);
                    if (predictionResult.Prediction)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                    }
                    Console.WriteLine($" ({predictionResult.Probability * 100:0.0}%, {predictionResult.Prediction}, {predictionResult.Score})");
                    if (newEvents && predictionResult.Probability > 0.7 && notificationRegions.Contains(group))
                    {
                        client.SendMessageAsync(new InputChannel(tryvogaPrediction.id, tryvogaPrediction.access_hash),
                            $"{group} область - ймовірність {predictionResult.Probability * 100:0.0}%");
                    }
                    if (newEvents && predictionResult.Probability > 0.7)
                    {
                        client.SendMessageAsync(new InputChannel(tryvogaPredictionTest.id, tryvogaPredictionTest.access_hash),
                            $"{group} область - ймовірність {predictionResult.Probability * 100:0.0}%");
                    }
                }
                else { Console.WriteLine("."); }

            }
            Console.ForegroundColor = color;
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

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Dictionary<int, TryvohaEvent> events = LoadFromFile();
            var predictionEngines = events.Count > 0
                ? GetPredictionEngines(events, true)
                : new Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>>();
            Console.WriteLine(posAvg.Average());
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

            init = false;
            bool regenerating = false;
            while (true)
            {
                int eventsCount = events.Count;
                FillInEvents(client, tryvogaChannel, events);
                bool newEvents = eventsCount != events.Count;
                ShowPredictionMessage(client, predictionEngines, events, tryvogaPredictionChannel, tryvogaPredictionTest, newEvents);

                if (DateTime.Now.Minute == 15 && !regenerating)
                {
                    regenerating = true;
                    predictionEngines = GetPredictionEngines(events, true);
                }
                if (DateTime.Now.Minute == 16)
                {
                    regenerating = false;
                }
                Thread.Sleep(10000);
            }
        }
    }
}