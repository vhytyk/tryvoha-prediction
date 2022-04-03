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

namespace Tryvoga
{
    class Program
    {
        static string path = "/Users/vic/tryvoha";
        static string fileName = $"{path}/tryvoha.csv";
        public class TryvohaEvent
        {
            [LoadColumn(0)]
            public int Id { get; set; }
            [LoadColumn(1)]
            public DateTime EventTime { get; set; }
            [LoadColumn(2)]
            public string Region { get; set; }
            [LoadColumn(3)]
            public bool OnOff { get; set; }
        }
        static string Config(string what)
        {
            switch (what)
            {
                case "api_id": return "17668141";
                case "api_hash": return "9bb93730780057ee0011798211bb3f14";
                case "phone_number": return "+380506752507";
                case "verification_code": Console.Write("Code: "); return Console.ReadLine();
                case "password": return "secret!";     // if user has enabled 2FA
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
        static void SaveToDb(Dictionary<int, TryvohaEvent> events)
        {

            File.WriteAllText(fileName, $"Id;EventTime;Region;OnOff{Environment.NewLine}");
            foreach (var tryvoha in events.Values.OrderBy(e => e.Id))
            {
                File.AppendAllText(fileName, $"{tryvoha.Id};{tryvoha.EventTime.ToString("yyyy-MM-dd HH:mm:ss")};{tryvoha.Region};{tryvoha.OnOff}{Environment.NewLine}");
            }
        }

        public static Dictionary<int, T> LoadFromDb<T>() where T : TryvohaEvent
        {
            Dictionary<int, T> result = new Dictionary<int, T>();
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
                    T e = Activator.CreateInstance<T>();
                    e.Id = int.Parse(fields[0]);
                    e.EventTime = DateTime.Parse(fields[1]);
                    e.Region = fields[2];
                    e.OnOff = bool.Parse(fields[3]);
                    result.Add(int.Parse(fields[0]), e);
                }
            }
            return result;
        }

        static void FillInEvents(WTelegram.Client client, Channel tryvoga, Dictionary<int, TryvohaEvent> events, IEnumerable<int> loadedKeys = null)
        {
            int msgCount = -1;
            while (msgCount < events.Count && (loadedKeys == null || !events.Keys.Intersect(loadedKeys).Any()))
            {
                Thread.Sleep(1000);
                msgCount = events.Count;
                GetEvents(client, tryvoga, events);
                Console.WriteLine($"events added {events.Count - msgCount}, total count: {events.Count}");
            }
            SaveToDb(events);
        }

        static void ShowRegions(Dictionary<int, TryvohaEvent> events)
        {
            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key);
            ConsoleColor color = Console.ForegroundColor;
            foreach (var group in grouped)
            {
                var last = events.Values.OrderBy(e => e.EventTime).Last(e => e.Region == group);
                Console.ForegroundColor = last.OnOff ? ConsoleColor.Red : ConsoleColor.Green;
                Console.WriteLine($"{group}: {(last.OnOff ? "тривога" : "немає")}");
            }
            Console.ForegroundColor = color;
        }

        public class TrainRecord
        {
            [LoadColumn(0)]
            public string RegionsOn { get; set; }
            [LoadColumn(1), ColumnName("Label")]
            public bool Min10 { get; set; }
        }
        public class PredictionRecord : TrainRecord
        {

            [ColumnName("PredictedLabel")]
            public bool Prediction { get; set; }

            public float Probability { get; set; }

            public float Score { get; set; }
        }
        public static ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {
           
            var estimator = mlContext.Transforms.Text.FeaturizeText(outputColumnName: "Features", inputColumnName: nameof(TrainRecord.RegionsOn))            
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

        static void GenerateData(string region)
        {
            Console.Write($"Generating train set for {region}...");
            File.WriteAllText($"{path}/{region}.csv", $"RegionsOn;Min10{Environment.NewLine}");
            Dictionary<int, TryvohaEvent> events = LoadFromDb<TryvohaEvent>();
            foreach (var ev in events.Values)
            {
                var previousEvents = events.Values.Where(e => e.Id <= ev.Id);
                var grouped = previousEvents.GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
                }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
                var r = new TrainRecord
                {
                    RegionsOn = string.Join(',', grouped.Select(g => g.Region)),
                    Min10 = events.Values.Where(e => e.EventTime > ev.EventTime && e.EventTime <= ev.EventTime.AddMinutes(10) && e.Region == region && e.OnOff).Any()
                };
                File.AppendAllText($"{path}/{region}.csv", $"{r.RegionsOn};{(r.Min10 ? 1 : 0)}{Environment.NewLine}");
            }
            Console.WriteLine("done");
        }

        static PredictionEngine<TrainRecord, PredictionRecord> CreatePredictionEngine(string region)
        {
            Console.Write($"Creating prediction engine for {region}...");
            MLContext mlContext = new MLContext();
            IDataView dataView = mlContext.Data.LoadFromTextFile<TrainRecord>($"{path}/{region}.csv", hasHeader: true, separatorChar: ';');
            TrainTestData splitDataView = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);
            var model = BuildAndTrainModel(mlContext, splitDataView.TrainSet);
            var eval = Evaluate(mlContext, model, splitDataView.TestSet);
            Console.WriteLine($"{eval.Accuracy}%");
            return mlContext.Model.CreatePredictionEngine<TrainRecord, PredictionRecord>(model);
        }

        public static void Main(string[] args)
        {
            
            GenerateData("Закарпатська");
            GenerateData("Львівська");
            //return;
            
            var zakPre = CreatePredictionEngine("Закарпатська");
            var lvPre = CreatePredictionEngine("Львівська");

            Console.OutputEncoding = Encoding.UTF8;
            Dictionary<int, TryvohaEvent> events = LoadFromDb<TryvohaEvent>();
            using var client = new WTelegram.Client(Config);
            var my = client.LoginUserIfNeeded().Result;
            var x = client.Messages_GetAllChats().Result;
            Channel tryvoga = (Channel)x.chats[1766138888];
            Channel tryvogaPrediction = (Channel)x.chats[1766772788];
            Dictionary<int, TryvohaEvent> initialEvents = new Dictionary<int, TryvohaEvent>();

            if (events.Count > 0)
            {
                Console.WriteLine($"loaded from db: {events.Count}. Reading new events.");
                FillInEvents(client, tryvoga, initialEvents, events.Keys);

                foreach (var e in initialEvents)
                {
                    if (!events.ContainsKey(e.Key))
                    {
                        events.Add(e.Key, e.Value);
                    }
                }
            }
            
            init = false;
            while (true)
            {
                int eventsCount = events.Count;
                FillInEvents(client, tryvoga, events);
                bool newEvents = eventsCount != events.Count;
                ShowRegions(events);
                var grouped = events.Values.GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
                }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
                TrainRecord sampleStatement = new TrainRecord
                {
                    RegionsOn = string.Join(',', grouped.Select(g => g.Region))
                };
                var zakRes = zakPre.Predict(sampleStatement);
                var lvRes = lvPre.Predict(sampleStatement);
                Console.WriteLine("----");
                Console.WriteLine($"10хв ймовірність на Закарпатській - {zakRes.Probability * 100:0.0}%");
                Console.WriteLine($"10хв ймовірність у Львівській - {lvRes.Probability * 100:0.0}%");
                if (newEvents && zakRes.Probability > 0.7)
                {
                    client.SendMessageAsync(new InputChannel(tryvogaPrediction.id, tryvogaPrediction.access_hash), $"10хв ймовірність на Закарпатській - {zakRes.Probability * 100:0.0}%");
                }
                if (newEvents && lvRes.Probability > 0.7)
                {
                    client.SendMessageAsync(new InputChannel(tryvogaPrediction.id, tryvogaPrediction.access_hash), $"10хв ймовірність у Львівській - {lvRes.Probability * 100:0.0}%");
                }

                Thread.Sleep(10000);
            }

        }
    }
}