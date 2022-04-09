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
    public class TryvohaPredictionServiceOff
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
        private List<double> _loss = new List<double>();
        private List<double> _rsqr = new List<double>();
        private Dictionary<string, PredictionEngine<OffTryvohaTrainingRecord, OffTryvohaPredictionRecord>> _predictionEngines 
            = new Dictionary<string, PredictionEngine<OffTryvohaTrainingRecord, OffTryvohaPredictionRecord>>();

        private const int minStep = 1;
        int GetTimeDiff(DateTime to, DateTime from)
        {
            var timeDiff = (to - from).TotalMinutes;
            var timeDiffRes = ((int)(timeDiff / minStep)) * minStep;
            if (timeDiff > 120)
            {
                return 200;
            }
            return timeDiffRes;
        }

        void GenerateData(string region)
        {
            Console.Write($"Generating train set (off) for {region}...");
            Dictionary<int, TryvohaEvent> events = Program.LoadFromFile();
            File.WriteAllText($"{Program.DataPath}/{region}Off.csv", $"Id;RegionsOn;DiffMins{Environment.NewLine}");

            foreach (var ev in events.Values.OrderBy(e => e.Id).Where(e => e.Region == region && e.OnOff))
            {
                var current = ev.EventTime;
                var end = events.Values.OrderBy(e => e.EventTime)
                    .FirstOrDefault(e => e.Id > ev.Id && e.Region == region && !e.OnOff)?.EventTime ?? DateTime.UtcNow;
                if (GetTimeDiff(end, current) == 200)
                {
                    continue;
                }
                var previousEvents = events.Values.Where(e => e.Id <= ev.Id);
                var groupedOn = previousEvents.Where(e => e.Region != region && e.EventTime >= ev.EventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
                }).OrderBy(g => g.EventTime);
                while (current < end)
                {
                    int timeDiffRegion = GetTimeDiff(end, current);
                    var r = new OffTryvohaTrainingRecord
                    {
                        RegionsOn = string.Join(" ", groupedOn.Select(g => $"{_regionsSmall[g.Region]}{GetTimeDiff(current, g.EventTime)}")),
                        DiffMins = timeDiffRegion
                    };
                    File.AppendAllText($"{Program.DataPath}/{region}Off.csv", $"{ev.Id};{r.RegionsOn};{(r.DiffMins)}{Environment.NewLine}");
                    current = current.AddMinutes(minStep);
                }
               
            }
            Console.WriteLine("done");
        }
        ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {

            var estimator = mlContext.Transforms.Text.FeaturizeText(outputColumnName: "Features", inputColumnName: nameof(OffTryvohaTrainingRecord.RegionsOn))
             .Append(mlContext.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "Features"));
            var model = estimator.Fit(splitTrainSet);

            return model;
        }

        RegressionMetrics Evaluate(MLContext mlContext, ITransformer model, IDataView splitTestSet)
        {
            IDataView predictions = model.Transform(splitTestSet);

            RegressionMetrics metrics = mlContext.Regression.Evaluate(predictions, "Label");

            return metrics;
        }

        PredictionEngine<OffTryvohaTrainingRecord, OffTryvohaPredictionRecord> CreatePredictionEngine(string region, bool regenerate = false)
        {
            Console.Write($"Creating prediction engine (Off) for {region}...");
            MLContext mlContext = new MLContext();
            IDataView dataView = mlContext.Data.LoadFromTextFile<OffTryvohaTrainingRecord>($"{Program.DataPath}/{region}Off.csv", hasHeader: true, separatorChar: ';');
            DataOperationsCatalog.TrainTestData splitDataView = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

            string engineFileName = $"{Program.DataPath}/{region}Off.zip";
            var model = File.Exists(engineFileName) && !regenerate
                ? mlContext.Model.Load(engineFileName, out _)
                : BuildAndTrainModel(mlContext, splitDataView.TrainSet);
            if (!File.Exists(engineFileName) || regenerate)
            {
                mlContext.Model.Save(model, dataView.Schema, engineFileName);
            }
            var eval = Evaluate(mlContext, model, splitDataView.TestSet);
            Console.WriteLine($"loss: {eval.LossFunction:0.0}, rsqr: {eval.RSquared:0.00}");
            _rsqr.Add(eval.RSquared);
            _loss.Add(eval.LossFunction);
            return mlContext.Model.CreatePredictionEngine<OffTryvohaTrainingRecord, OffTryvohaPredictionRecord>(model);
        }

        public void GeneratePredictionEngines(Dictionary<int, TryvohaEvent> events, bool regenerate = false)
        {
            _rsqr.Clear();
            _loss.Clear();
            var regions = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key)
                .Where(e => e != "Херсонська" && e != "Луганська").OrderBy(e => e);

            foreach (string region in regions)
            {
                if (regenerate || !File.Exists($"{Program.DataPath}/{region}Off.csv"))
                {
                    GenerateData(region);
                }
                _predictionEngines[region] = CreatePredictionEngine(region, regenerate);
            }
        }

        public Tuple<double, double> GetModelEvaluationsAvg()
        {
            if (_loss.Any() && _rsqr.Any())
            {
                return new Tuple<double, double>(_rsqr.Average(), _loss.Average());
            }

            return null;
        }

        public Dictionary<string, OffTryvohaPredictionRecord> ProcessPrediction(WTelegram.Client client,
           Dictionary<int, TryvohaEvent> events,
           Channel tryvogaPrediction,
           Channel tryvogaPredictionTest,
           Dictionary<int, TryvohaEvent> newEvents)
        {
            Dictionary<string, OffTryvohaPredictionRecord> result = new Dictionary<string, OffTryvohaPredictionRecord>();
            var lastEventTime = events.Values.OrderBy(e => e.Id).LastOrDefault()?.EventTime ?? DateTime.UtcNow;
            var groupedForPrediction = events.Values.Where(e => e.EventTime >= lastEventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.OnOff ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
            }).OrderBy(g => g.EventTime);
            OffTryvohaTrainingRecord sampleStatement = new OffTryvohaTrainingRecord
            {
                RegionsOn = string.Join(" ", groupedForPrediction.Select(g => $"{_regionsSmall[g.Region]}{GetTimeDiff(DateTime.UtcNow, g.EventTime)}"))
            };
            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key).OrderBy(e => e);
            foreach (var region in grouped)
            {
                var last = events.Values.OrderBy(e => e.EventTime).Last(e => e.Region == region);
                
                if (_predictionEngines.ContainsKey(region) && last.OnOff)
                {
                    var predictionResult = _predictionEngines[region].Predict(sampleStatement);
                    result[region] = predictionResult;
                }
            }
            
            double modelsAgeMins = _predictionEngines.Any()
                ? (DateTime.UtcNow - File.GetLastWriteTimeUtc($"{Program.DataPath}/{_predictionEngines.Keys.First()}Off.zip")).TotalMinutes
                : double.MaxValue;
            if (modelsAgeMins > 60)
            {
                GeneratePredictionEngines(events, regenerate: true);
            }

            return result;
        }
    }
}
