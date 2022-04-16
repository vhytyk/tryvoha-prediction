using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TL;

namespace TryvogaPrediction
{
    public class TgService
    {
        private TgEventsStorageService _eventsStorage;
        private WTelegram.Client _client;
        private Channel _tryvogaChannel;
        private Channel _tryvogaPredictionChannel;
        private Channel _tryvogaPredictionTest;
        public TgService(TgEventsStorageService eventsStorage)
        {
            _eventsStorage = eventsStorage;
        }

        public void Connect()
        {
            _client = new WTelegram.Client(ConfigForTelegramClient);
            _ = _client.LoginUserIfNeeded().Result;
            Messages_Chats allChats = _client.Messages_GetAllChats().Result;
            _tryvogaChannel = (TL.Channel)allChats.chats[1766138888];
            _tryvogaPredictionChannel = (Channel)allChats.chats[1766772788];
            _tryvogaPredictionTest = (Channel)allChats.chats[1660739731];
        }

        string ConfigForTelegramClient(string what)
        {
            switch (what)
            {
                case "api_id": return Config.TgApiId;
                case "api_hash": return Config.TgApiHash;
                case "phone_number": return Config.TgPhoneNumber;
                case "verification_code": Console.Write("Code: "); return Console.ReadLine();
                case "password": return Config.TgPassword;
                default: return null;
            }
        }

        public void FillHistoricalEvents()
        {
            Console.WriteLine($"{Environment.NewLine}{DateTime.Now}: getting new events...");
            int msgCount = -1;
            var events = _eventsStorage.GetEvents();
            while (msgCount < events.Count && (loadedKeys == null || !events.Keys.Intersect(loadedKeys).Any()))
            {
                System.Threading.Thread.Sleep(1000);
                msgCount = events.Count;
                GetEvents(client, tryvoga, events);
                Console.WriteLine($"events added {events.Count - msgCount}, total count: {events.Count}");
            }
            SaveToFile(events);
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
        static void GetEvents(WTelegram.Client client, Channel tryvoga, Dictionary<int, TryvohaEvent> events)
        {
            try
            {
                DateTime minDateTime = _init && events.Count > 0 ? events.Values.Min(v => v.EventTime) : DateTime.UtcNow;

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
    }
}
