using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.Data;
using TL;

namespace TryvogaPrediction
{
    public class TryvohaPredictionServiceOn
    {
        private Dictionary<string, string> _regionsSmall = new Dictionary<string, string>
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
        private List<double> _pos = new List<double>();
        private List<double> _f1 = new List<double>();
        private List<double> _acc = new List<double>();
        private Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>> _predictionEngines 
            = new Dictionary<string, PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>>();

        int GetTimeDiff(DateTime to, DateTime from)
        {
            var timeDiff = (to - from).TotalMinutes;
            if (timeDiff > 120)
            {
                return 200;
            }
            return (int)timeDiff;
        }

        void GenerateData(string region)
        {
            Console.Write($"Generating train set (on) for {region}...");
            Dictionary<int, TryvohaEvent> events = Program.LoadFromFile()
                .Where(e => e.Value.EventTime > DateTime.UtcNow.AddMonths(-1))
                .ToDictionary(e => e.Key, e => e.Value);
            File.WriteAllText($"{Program.DataPath}/{region}On.csv", $"RegionsOn;Min10{Environment.NewLine}");

            foreach (var ev in events.Values.OrderBy(e => e.Id).Where(e => e.OnOff && e.Region != region))
            {
                var previousEvents = events.Values.Where(e => e.Id <= ev.Id);
                var grouped = previousEvents.Where(e => e.EventTime >= ev.EventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue,
                    PreviousEventTime = e.OrderBy(i => i.Id).TakeLast(2).FirstOrDefault()?.EventTime ?? DateTime.MinValue
                }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
                if (grouped.Any(g => g.Region == region))
                {
                    continue;
                }
                var r = new TryvohaTrainingRecord
                {
                    RegionsOn = string.Join(" ", grouped.Select(g => $"{_regionsSmall[g.Region]}{GetTimeDiff(g.PreviousEventTime, g.EventTime)}")),
                    Min10 = events.Values.Any(e => e.EventTime > ev.EventTime && e.EventTime <= ev.EventTime.AddMinutes(20) && e.Region == region && e.OnOff)
                };
                File.AppendAllText($"{Program.DataPath}/{region}On.csv", $"{ev.Id};{r.RegionsOn};{(r.Min10 ? 1 : 0)}{Environment.NewLine}");
            }
            Console.WriteLine("done");
        }
        ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {

            var estimator = mlContext.Transforms.Text.FeaturizeText(outputColumnName: "Features", inputColumnName: nameof(TryvohaTrainingRecord.RegionsOn))
             .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(labelColumnName: "Label", featureColumnName: "Features"));

            var model = estimator.Fit(splitTrainSet);

            return model;
        }

        CalibratedBinaryClassificationMetrics Evaluate(MLContext mlContext, ITransformer model, IDataView splitTestSet)
        {
            IDataView predictions = model.Transform(splitTestSet);

            CalibratedBinaryClassificationMetrics metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");

            return metrics;
        }

        PredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord> CreatePredictionEngine(string region, bool regenerate = false)
        {
            Console.Write($"Creating prediction engine (on) for {region}...");
            MLContext mlContext = new MLContext();
            IDataView dataView = mlContext.Data.LoadFromTextFile<TryvohaTrainingRecord>($"{Program.DataPath}/{region}On.csv", hasHeader: true, separatorChar: ';');
            DataOperationsCatalog.TrainTestData splitDataView = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

            string engineFileName = $"{Program.DataPath}/{region}On.zip";
            var model = File.Exists(engineFileName) && !regenerate
                ? mlContext.Model.Load(engineFileName, out _)
                : BuildAndTrainModel(mlContext, splitDataView.TrainSet);
            if (!File.Exists(engineFileName) || regenerate)
            {
                mlContext.Model.Save(model, dataView.Schema, engineFileName);
            }
            var eval = Evaluate(mlContext, model, splitDataView.TestSet);
            Console.WriteLine($" acc: {eval.Accuracy:0.00}, posrec: {eval.PositiveRecall:0.00}, f1: {eval.F1Score:0.00}");
            _pos.Add(eval.PositiveRecall);
            _f1.Add(eval.F1Score);
            _acc.Add(eval.Accuracy);
            return mlContext.Model.CreatePredictionEngine<TryvohaTrainingRecord, TryvohaPredictionRecord>(model);
        }

        public void GeneratePredictionEngines(Dictionary<int, TryvohaEvent> events, bool regenerate = false)
        {
            _pos.Clear();
            _acc.Clear();
            _f1.Clear();
            var regions = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key)
                .Where(e => e != "Херсонська" && e != "Луганська").OrderBy(e => e);

            foreach (string region in regions)
            {
                if (regenerate || !File.Exists($"{Program.DataPath}/{region}On.csv"))
                {
                    GenerateData(region);
                }
                _predictionEngines[region] = CreatePredictionEngine(region, regenerate);
            }
        }

        public Tuple<double, double, double> GetModelEvaluationsAvg()
        {
            if (_acc.Any() && _pos.Any() && _f1.Any())
            {
                return new Tuple<double, double, double>(_acc.Average(), _pos.Average(), _f1.Average());
            }

            return null;
        }

        public Dictionary<string, TryvohaPredictionRecord> ProcessPrediction(WTelegram.Client client,
           Dictionary<int, TryvohaEvent> events,
           Channel tryvogaPrediction,
           Channel tryvogaPredictionTest,
           Dictionary<int, TryvohaEvent> newEvents)
        {
            Dictionary<string, TryvohaPredictionRecord> result = new Dictionary<string, TryvohaPredictionRecord>();
            var lastEventTime = events.Values.OrderBy(e => e.Id).LastOrDefault()?.EventTime ?? DateTime.UtcNow;
            string[] notificationRegions =  { "Закарпатська", "Львівська", "Івано-Франківська" };
            var groupedForPrediction = events.Values.Where(e => e.EventTime >= lastEventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue,
                PreviousEventTime = e.OrderBy(i => i.Id).TakeLast(2).FirstOrDefault()?.EventTime ?? DateTime.MinValue
            }).Where(g => g.OnOff).OrderBy(g => g.EventTime);
            TryvohaTrainingRecord sampleStatement = new TryvohaTrainingRecord
            {
                RegionsOn = string.Join(" ", groupedForPrediction.Select(g => $"{_regionsSmall[g.Region]}{GetTimeDiff(g.PreviousEventTime, g.EventTime)}"))
            };
            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key).OrderBy(e => e);
            foreach (var region in grouped)
            {
                var last = events.Values.Where(e => e.Region == region).OrderBy(e => e.EventTime).Last();
                var previous = events.Values.Where(e => e.Region == region).OrderBy(e => e.EventTime).TakeLast(2).First();

                if (_predictionEngines.ContainsKey(region) && !last.OnOff)
                {
                    var predictionResult = _predictionEngines[region].Predict(sampleStatement);
                    result[region] = predictionResult;

                    if (Program.SendNotifications && previous.EventTime < DateTime.UtcNow.AddHours(-1) && predictionResult.Prediction && notificationRegions.Contains(region))
                    {
                        client.SendMessageAsync(new InputChannel(tryvogaPrediction.id, tryvogaPrediction.access_hash),
                            $"{region} область - ймовірність {predictionResult.Probability * 100:0.0}%");
                    }
                    if (Program.SendNotifications && previous.EventTime < DateTime.UtcNow.AddHours(-1) && predictionResult.Prediction)
                    {
                        client.SendMessageAsync(new InputChannel(tryvogaPredictionTest.id, tryvogaPredictionTest.access_hash),
                            $"{region} область - ймовірність {predictionResult.Probability * 100:0.0}%");
                    }
                }

            }

            double modelsAgeMins = _predictionEngines.Any()
                ? (DateTime.UtcNow - File.GetLastWriteTimeUtc($"{Program.DataPath}/{_predictionEngines.Keys.First()}On.zip")).TotalMinutes
                : double.MaxValue;
            if (modelsAgeMins > 60)
            {
                GeneratePredictionEngines(events, regenerate: true);
            }

            return result;
        }
    }
}
