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
        private List<double> _loss = new List<double>();
        private List<double> _rsqr = new List<double>();
        private List<double> _err = new List<double>();
        private Dictionary<string, PredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord>> _predictionEngines
            = new Dictionary<string, PredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord>>();

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
            Dictionary<int, TryvohaEvent> events = Program.LoadFromFile()
                .Where(e => e.Value.EventTime > DateTime.UtcNow.AddMonths(-1))
                .ToDictionary(e => e.Key, e => e.Value);
            File.WriteAllText($"{Program.DataPath}/{region}Off.csv", $"Id;RegionsOn;DiffMins{Environment.NewLine}");

            foreach (var current in events.Values.OrderBy(e => e.Id).Where(e => e.Region == region && e.Tryvoha))
            {
                var currentTime = current.EventTime;
                var end = events.Values.OrderBy(e => e.EventTime)
                    .FirstOrDefault(e => e.Id > current.Id && e.Region == region && !e.Tryvoha)?.EventTime ?? DateTime.UtcNow;
                if (GetTimeDiff(end, currentTime) == 200)
                {
                    continue;
                }
                var previousEvents = events.Values.Where(e => e.Id <= current.Id);
                string[] regionGroups = Program.RegionsGroups.First(g => g.Value.Contains(region)).Value;
                var grouped = previousEvents.Where(e => e.Region != region && e.EventTime >= currentTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
                {
                    Region = e.Key,
                    Tryvoha = e.OrderBy(i => i.Id).LastOrDefault()?.Tryvoha ?? false,
                    EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
                }).OrderBy(g => g.EventTime);
                while (currentTime <= end)
                {
                    var groupedOn = grouped.Where(g => g.Tryvoha);
                    var groupedOffRecently = grouped.Where(g => !g.Tryvoha && g.EventTime > currentTime.AddMinutes(-30));
                    var closeGroupedOn = grouped.Where(g => g.Tryvoha && regionGroups.Contains(g.Region));
                    var closeGroupedOffRecently = grouped.Where(g => !g.Tryvoha && g.EventTime > currentTime.AddMinutes(-30) && regionGroups.Contains(g.Region));
                    int timeDiffRegion = GetTimeDiff(end, currentTime);
                    var r = new TryvohaOffTrainingRecord
                    {
                        //RegionsOn = string.Join(" ", groupedOn.Select(g => $"{Program.RegionsPlates[g.Region]}{GetTimeDiff(currentTime, g.EventTime)}")),
                        RegionsOnCount = groupedOn.Count(),
                        RegionsOnMinutes = groupedOn.Sum(g => GetTimeDiff(currentTime, g.EventTime)),
                        RegionsRecentlyOffCount = groupedOffRecently.Count(),
                        RegionsRecentlyOffMinutes = groupedOffRecently.Sum(g => GetTimeDiff(currentTime, g.EventTime)),
                        CloseRegionsOnCount = closeGroupedOn.Count(),
                        CloseRegionsOnMinutes = closeGroupedOn.Sum(g => GetTimeDiff(currentTime, g.EventTime)),
                        CloseRegionsRecentlyOffCount = closeGroupedOffRecently.Count(),
                        CloseRegionsRecentlyOffMinutes = closeGroupedOffRecently.Sum(g => GetTimeDiff(currentTime, g.EventTime)),
                        DiffMins = timeDiffRegion
                    };
                    //File.AppendAllText($"{Program.DataPath}/{region}Off.csv", $"{current.Id};{r.RegionsOn};{(r.DiffMins)}{Environment.NewLine}");
                    File.AppendAllText($"{Program.DataPath}/{region}Off.csv", $"{current.Id};{r.RegionsOnCount};{r.RegionsOnMinutes};{r.RegionsRecentlyOffCount};{r.RegionsRecentlyOffMinutes};{r.CloseRegionsOnCount};{r.CloseRegionsOnMinutes};{r.CloseRegionsRecentlyOffCount};{r.CloseRegionsRecentlyOffMinutes};{(r.DiffMins)}{Environment.NewLine}");
                    currentTime = currentTime.AddMinutes(minStep);
                }

            }
            Console.WriteLine("done");
        }
        ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {
            var estimator = mlContext.Transforms.Concatenate(outputColumnName: "Features",
                                nameof(TryvohaOffTrainingRecord.RegionsOnCount),
                                nameof(TryvohaOffTrainingRecord.RegionsOnMinutes),
                                nameof(TryvohaOffTrainingRecord.RegionsRecentlyOffCount),
                                nameof(TryvohaOffTrainingRecord.RegionsRecentlyOffMinutes),
                                nameof(TryvohaOffTrainingRecord.CloseRegionsOnCount),
                                nameof(TryvohaOffTrainingRecord.CloseRegionsOnMinutes),
                                nameof(TryvohaOffTrainingRecord.CloseRegionsRecentlyOffCount),
                                nameof(TryvohaOffTrainingRecord.CloseRegionsRecentlyOffMinutes))
                .Append(mlContext.Transforms.NormalizeMeanVariance("Features",
                            useCdf: true))
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

        PredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord> CreatePredictionEngine(string region, bool regenerate = false)
        {
            Console.Write($"Creating prediction engine (Off) for {region}...");
            MLContext mlContext = new MLContext();
            IDataView dataView = mlContext.Data.LoadFromTextFile<TryvohaOffTrainingRecord>($"{Program.DataPath}/{region}Off.csv", hasHeader: true, separatorChar: ';');
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
            Console.WriteLine($"loss: {eval.LossFunction:0.0}, rsqr: {eval.RSquared:0.00}, mae: {eval.MeanAbsoluteError:0.0}");
            _rsqr.Add(eval.RSquared);
            _loss.Add(eval.LossFunction);
            _err.Add(eval.MeanAbsoluteError);
            return mlContext.Model.CreatePredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord>(model);
        }

        public void GeneratePredictionEngines(Dictionary<int, TryvohaEvent> events, bool regenerate = false)
        {
            _rsqr.Clear();
            _loss.Clear();
            _err.Clear();

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

        public Tuple<double, double, double> GetModelEvaluationsAvg()
        {
            if (_loss.Any() && _rsqr.Any() && _err.Any())
            {
                return new Tuple<double, double, double>(_rsqr.Average(), _loss.Average(), _err.Average());
            }

            return null;
        }

        public Dictionary<string, TryvohaOffPredictionRecord> ProcessPrediction(WTelegram.Client client,
           Dictionary<int, TryvohaEvent> events,
           Channel tryvogaPrediction,
           Channel tryvogaPredictionTest,
           Dictionary<int, TryvohaEvent> newEvents)
        {
            Dictionary<string, TryvohaOffPredictionRecord> result = new Dictionary<string, TryvohaOffPredictionRecord>();
            var lastEventTime = events.Values.OrderBy(e => e.Id).LastOrDefault()?.EventTime ?? DateTime.UtcNow;
            var groupedForPrediction = events.Values.Where(e => e.EventTime >= lastEventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                OnOff = e.OrderBy(i => i.Id).LastOrDefault()?.Tryvoha ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
            }).OrderBy(g => g.EventTime);
            TryvohaOffTrainingRecord sampleStatement = new TryvohaOffTrainingRecord
            {
                //RegionsOn = string.Join(" ", groupedForPrediction.Select(g => $"{Program.RegionsPlates[g.Region]}{GetTimeDiff(DateTime.UtcNow, g.EventTime)}"))
            };
            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key).OrderBy(e => e);
            foreach (var region in grouped)
            {
                var last = events.Values.OrderBy(e => e.EventTime).Last(e => e.Region == region);

                if (_predictionEngines.ContainsKey(region) && last.Tryvoha)
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
