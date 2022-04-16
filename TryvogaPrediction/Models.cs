using System;
using System.Collections.Generic;
using Microsoft.ML.Data;

namespace TryvogaPrediction
{
    public class TryvohaEvent
    {
        public int Id { get; set; }
        public DateTime EventTime { get; set; }
        public string Region { get; set; }
        public bool Tryvoha { get; set; }
    }

    public class TryvohaTrainingRecord
    {
        [LoadColumn(0)]
        public int Id { get; set; }
        [LoadColumn(1)]
        public string RegionsOn { get; set; }
        [LoadColumn(2), ColumnName("Label")]
        public bool Min10 { get; set; }
    }
    public class TryvohaPredictionRecord : TryvohaTrainingRecord
    {

        [ColumnName("PredictedLabel")]
        public bool Prediction { get; set; }

        public float Probability { get; set; }

        public float Score { get; set; }
    }

    public class TryvohaOffTrainingRecord
    {
        [LoadColumn(0)]
        public int Id { get; set; }
        [LoadColumn(1)]
        public string RegionsOn { get; set; }
        [LoadColumn(2), ColumnName("Label")]
        public Single DiffMins { get; set; }

        //[LoadColumn(1)]
        //public Single RegionsOnCount { get; set; }
        //[LoadColumn(2)]
        //public Single RegionsOnMinutes { get; set; }
        //[LoadColumn(3)]
        //public Single RegionsRecentlyOffCount { get; set; }
        //[LoadColumn(4)]
        //public Single RegionsRecentlyOffMinutes { get; set; }
        //[LoadColumn(5)]
        //public Single CloseRegionsOnCount { get; set; }
        //[LoadColumn(6)]
        //public Single CloseRegionsOnMinutes { get; set; }
        //[LoadColumn(7)]
        //public Single CloseRegionsRecentlyOffCount { get; set; }
        //[LoadColumn(8)]
        //public Single CloseRegionsRecentlyOffMinutes { get; set; }
        //[LoadColumn(9), ColumnName("Label")]
        //public Single DiffMins { get; set; }
    }

    public class TryvohaOffPredictionRecord : TryvohaOffTrainingRecord
    {
        public float Score { get; set; }
    }

    public class RegionStatus
    {
        public bool Status { get; set; }
        public bool? PredictedOn { get; set; }
        public double? PredictedOffMinutes { get; set; }
        public double? ProbabilityOn { get; set; }
    }

    public class ResultPayload
    {
        public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
        public Dictionary<string, RegionStatus> Regions { get; set; }
        public List<string> ModelEvaluations { get; set; }
    }
}
