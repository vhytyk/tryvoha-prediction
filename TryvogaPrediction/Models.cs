using System;
using Microsoft.ML.Data;

namespace TryvogaPrediction
{
    public class TryvohaEvent
    {
        public int Id { get; set; }
        public DateTime EventTime { get; set; }
        public string Region { get; set; }
        public bool OnOff { get; set; }
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
}
