using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.Data;
using TL;
using System.Numerics;

namespace TryvogaPrediction
{
    public class TryvohaPredictionServiceOff
    {
        private List<double> _loss = new List<double>();
        private List<double> _rsqr = new List<double>();
        private List<double> _err = new List<double>();
        private Dictionary<string, PredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord>> _predictionEngines
            = new Dictionary<string, PredictionEngine<TryvohaOffTrainingRecord, TryvohaOffPredictionRecord>>();

        private const float minStep = 1f;
        private const int limit = 90;
        float GetTimeDiff(DateTime to, DateTime from)
        {
            var timeDiff = (to - from).TotalMinutes;
            var timeDiffRes = ((float)(timeDiff / minStep)) * minStep;
            if (timeDiff > limit - 1)
            {
                return limit;
            }
            return timeDiffRes;
        }

        List<float> GetFeatures(DateTime currentTime, TryvohaEvent current, Dictionary<int, TryvohaEvent> events)
        {
            string[] closeGroup = Program.RegionsGroups.First(g => g.Value.Contains(current.Region)).Value.ToArray();
            var previousEvents = events.Values.Where(e => e.EventTime <= currentTime);
            var grouped = previousEvents.GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                Tryvoha = e.OrderBy(i => i.Id).LastOrDefault()?.Tryvoha ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
            }).ToList();

            var allGrouped = Program.RegionsPlates.Keys.Select(g => grouped.FirstOrDefault(gr => gr.Region == g) ?? new { Region = g, Tryvoha = false, EventTime = DateTime.MinValue }).ToList();
            List<float> features = allGrouped.OrderBy(g => g.Region).Select(r => GetTimeDiff(currentTime, r.EventTime) * (r.Tryvoha ? 1 : -1)).ToList();
            features.Add(currentTime.Hour);
            features.Add((float)currentTime.DayOfWeek);
            features.Add(allGrouped.Count(g => g.Tryvoha));
            features.Add(allGrouped.Count(g => !g.Tryvoha && Math.Abs(GetTimeDiff(currentTime, g.EventTime)) < limit / 2));
            features.Add(allGrouped.Count(g => closeGroup.Contains(g.Region) && g.Tryvoha));
            features.Add(allGrouped.Count(g => closeGroup.Contains(g.Region) && !g.Tryvoha && Math.Abs(GetTimeDiff(currentTime, g.EventTime)) < limit / 2));
            return features;
        }

        void GenerateData(string region)
        {
          
            Console.Write($"Generating train set (off) for {region}...");
            Dictionary<int, TryvohaEvent> events = Program.LoadFromFile()
                .Where(e => e.Value.EventTime > DateTime.UtcNow.AddDays(-31))
                .ToDictionary(e => e.Key, e => e.Value);
            File.WriteAllText($"{Program.DataPath}/{region}Off.csv", $"Id;RegionsMinutes;DiffMins{Environment.NewLine}");
            int count = 0;
            int all = 0;
            
            foreach (var current in events.Values.Where(e => e.EventTime > DateTime.UtcNow.AddDays(-30) && e.Region == region && e.Tryvoha))
            {
                all++;
                var currentTime = current.EventTime;
                var end = events.Values.OrderBy(e => e.EventTime)
                    .FirstOrDefault(e => e.EventTime > currentTime && e.Region == region && !e.Tryvoha)?.EventTime;
                if(!end.HasValue || GetTimeDiff(end.Value, currentTime) == limit) {
                    count++;
                    continue;
                }
                while (currentTime <= end)
                {
                    float timeDiffRegion = GetTimeDiff(end.Value, currentTime);
                    List<float> features = GetFeatures(currentTime, current, events);
                    File.AppendAllText($"{Program.DataPath}/{region}Off.csv", $"{current.Id};{string.Join(';', features)};{timeDiffRegion}{Environment.NewLine}");
                    currentTime = currentTime.AddMinutes(minStep);
                }

            }
            Console.WriteLine($"done - {count}, {all}");
        }
        ITransformer BuildAndTrainModel(MLContext mlContext, IDataView splitTrainSet)
        {
            var estimator = mlContext.Transforms.Concatenate("RegionsMinutes", "RegionsMinutes")
                .Append(mlContext.Transforms.NormalizeMinMax("Features", "RegionsMinutes"))
                .Append(mlContext.Transforms.NormalizeMeanVariance("Features", "RegionsMinutes"))
                //.Append(mlContext.Transforms.Concatenate("Features", "RegionsMinutes"))
                .Append(mlContext.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "Features"));

           //var estimator = mlContext.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "RegionsMinutes");

            var model = estimator.Fit(splitTrainSet);
            File.WriteAllText($"{Program.DataPath}/preview.csv", "");
            var preview = model.Transform(splitTrainSet).Preview(5);
            foreach (var row in preview.RowView)
            {
                var rowValue = row.Values.First(d => d.Key == "Features");

                File.AppendAllText($"{Program.DataPath}/preview.csv", $"{string.Join(',', ((VBuffer<float>)rowValue.Value).GetValues().ToArray())}{Environment.NewLine}");
            }
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
            string[] onlyRegions = null;// { "Вінницька", "Закарпатська" };
            foreach (string region in regions)
            {
                if(onlyRegions!= null && !onlyRegions.Contains(region))
                {
                    continue;
                }
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

        public Dictionary<string, TryvohaOffPredictionRecord> ProcessPrediction(
           Dictionary<int, TryvohaEvent> events)
        {
            Dictionary<string, TryvohaOffPredictionRecord> result = new Dictionary<string, TryvohaOffPredictionRecord>();
            var lastEventTime = events.Values.OrderBy(e => e.Id).LastOrDefault()?.EventTime ?? DateTime.UtcNow;
            var groupedForPrediction = events.Values.Where(e => e.EventTime >= lastEventTime.AddHours(-3)).GroupBy(e => e.Region).Select(e => new
            {
                Region = e.Key,
                Tryvoha = e.OrderBy(i => i.Id).LastOrDefault()?.Tryvoha ?? false,
                EventTime = e.OrderBy(i => i.Id).LastOrDefault()?.EventTime ?? DateTime.MinValue
            }).OrderBy(g => g.EventTime);


            var groupedOn = groupedForPrediction.Where(g => g.Tryvoha);
            var groupedOffRecently = groupedForPrediction.Where(g => !g.Tryvoha && g.EventTime > DateTime.UtcNow.AddMinutes(-30));

            var grouped = events.GroupBy(e => e.Value.Region, e => e.Key).Select(e => e.Key).OrderBy(e => e);
            foreach (var region in grouped)
            {
                var last = events.Values.OrderBy(e => e.EventTime).Last(e => e.Region == region);
                if (_predictionEngines.ContainsKey(region) && last.Tryvoha && GetTimeDiff(DateTime.UtcNow, last.EventTime) < limit)
                {
                    var predictionResult = _predictionEngines[region].Predict(new TryvohaOffTrainingRecord
                    {
                        RegionsMinutes = GetFeatures(DateTime.UtcNow, last, events).ToArray()
                    });
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
