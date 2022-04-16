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
        //[LoadColumn(1)]
        //public string Regions { get; set; }
        //[LoadColumn(2)]
        //public string CloseRegionsOn { get; set; }
        //[LoadColumn(3)]
        //public string RegionsOff { get; set; }
        //[LoadColumn(4)]
        //public string CloseRegionsOff { get; set; }
        //[LoadColumn(2), ColumnName("Label")]
        //public Single DiffMins { get; set; }

        [LoadColumn(1, 24)]
        [VectorType(24)]
        public Single[] RegionsMinutes { get; set; }
        [LoadColumn(25), ColumnName("Label")]
        public Single DiffMins { get; set; }
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
