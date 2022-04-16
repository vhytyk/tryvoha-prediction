using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TryvogaPrediction
{
    public class TgEventsStorageService
    {
        Dictionary<int, TryvohaEvent> _events = new Dictionary<int, TryvohaEvent>();
        BlockingCollection<TryvohaEvent> _queue = new BlockingCollection<TryvohaEvent>(new ConcurrentQueue<TryvohaEvent>());
        private readonly object _locker = new object();
        public event EventHandler NewEventAdded;

        public void AddNewEvent(TryvohaEvent tryvohaEvent)
        {
            _queue.Add(tryvohaEvent);
        }

        public void AddNewEvents(List<TryvohaEvent> tryvohaEvents)
        {
            tryvohaEvents.ForEach(AddNewEvent);
        }

        public void Init()
        {
            LoadFromFile();
            Task.Run(() => HandleEvents());
        }

        public Dictionary<int, TryvohaEvent> GetEvents()
        {
            lock (_locker)
            {
                return new Dictionary<int, TryvohaEvent>(_events);
            }
        }
        void LoadFromFile()
        {
            lock (_locker)
            {
                _events = new Dictionary<int, TryvohaEvent>();
                if (!File.Exists(Config.DataFileName))
                {
                    return;
                }
                try
                {
                    using (TextFieldParser parser = new TextFieldParser(Config.DataFileName))
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
                            _events.Add(e.Id, e);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading events from file {Config.DataFileName}: {ex.Message}");
                }
            }
        }

        void HandleEvents()
        {
            while (true)
            {
                TryvohaEvent newEvent = _queue.Take();
                bool newEventAdded = false;

                lock (_locker)
                {
                    if (!_events.ContainsKey(newEvent.Id))
                    {
                        _events.Add(newEvent.Id, newEvent);
                        newEventAdded = true;
                    }
                }

                if (newEventAdded && NewEventAdded != null)
                {
                    NewEventAdded(this, EventArgs.Empty);
                }
            }
        }
        public void SaveToFile()
        {
            File.WriteAllText(Config.DataFileName, $"Id;EventTime;Region;OnOff{Environment.NewLine}");
            foreach (var tryvoha in _events.Values.OrderBy(e => e.Id))
            {
                File.AppendAllText(Config.DataFileName, $"{tryvoha.Id};{tryvoha.EventTime.ToString("yyyy-MM-dd HH:mm:ss")};{tryvoha.Region};{tryvoha.OnOff}{Environment.NewLine}");
            }
        }
    }
}
