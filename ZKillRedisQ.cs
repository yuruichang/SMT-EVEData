//-----------------------------------------------------------------------
// ZKillboard R2Z2 feed
//-----------------------------------------------------------------------
using System.ComponentModel;
using System.Net.Http;
using System.Net;
using Timer = System.Timers.Timer;

namespace SMT.EVEData
{
    /// <summary>
    /// The ZKillboard R2Z2 feed representation
    /// </summary>
    public class ZKillRedisQ
    {
        private BackgroundWorker backgroundWorker;

        private long currentSequence = 0;
        private DateTime nextPollTime = DateTime.MinValue;

        /// <summary>
        /// Gets or sets the Stream of the last few kills from ZKillBoard
        /// </summary>
        public List<ZKBDataSimple> KillStream { get; set; }

        /// <summary>
        /// Kills Added Event Handler
        /// </summary>
        public delegate void KillsAddedHandler();

        /// <summary>
        /// Kills Added Events
        /// </summary>
        public event KillsAddedHandler KillsAddedEvent;

        public int KillExpireTimeMinutes { get; set; }

        /// <summary>
        ///
        /// </summary>
        public bool PauseUpdate { get; set; }

        /// <summary>
        /// Initialise the ZKB feed system
        /// </summary>
        public void Initialise()
        {
            KillStream = new List<ZKBDataSimple>();

            backgroundWorker = new BackgroundWorker();
            backgroundWorker.WorkerSupportsCancellation = true;
            backgroundWorker.WorkerReportsProgress = false;
            backgroundWorker.DoWork += zkb_DoWork;
            backgroundWorker.RunWorkerCompleted += zkb_DoWorkComplete;

            Timer dp = new Timer(150);
            dp.Elapsed += Dp_Tick;
            dp.AutoReset = true;
            dp.Enabled = true;
        }

        public void ShutDown()
        {
            backgroundWorker.CancelAsync();
        }

        private void Dp_Tick(object sender, EventArgs e)
        {
            if(!backgroundWorker.IsBusy && !PauseUpdate && DateTime.Now >= nextPollTime)
            {
                backgroundWorker.RunWorkerAsync();
            }
        }

        private void zkb_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                HttpClient hc = new HttpClient();

                string userAgent = "SMT/" + EveAppConfig.SMT_VERSION + EveAppConfig.SMT_USERAGENT_DETAILS;
                hc.DefaultRequestHeaders.Add("User-Agent", userAgent);

                if (currentSequence == 0)
                {
                    string seqUrl = "https://r2z2.zkillboard.com/ephemeral/sequence.json";
                    var seqResponse = hc.GetAsync(seqUrl).Result;
                    if (seqResponse.IsSuccessStatusCode)
                    {
                        string seqContent = seqResponse.Content.ReadAsStringAsync().Result;
                        ZKBData.SequenceData seqData = ZKBData.SequenceData.FromJson(seqContent);
                        if (seqData != null)
                        {
                            currentSequence = seqData.Sequence + 1;
                        }
                    }
                    if (currentSequence == 0)
                    {
                        nextPollTime = DateTime.Now.AddSeconds(2);
                        e.Result = 0;
                        return;
                    }
                }

                string r2z2Url = $"https://r2z2.zkillboard.com/ephemeral/{currentSequence}.json";
                var response = hc.GetAsync(r2z2Url).Result;

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    nextPollTime = DateTime.Now.AddSeconds(2);
                    e.Result = 0;
                    return;
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    nextPollTime = DateTime.Now.AddSeconds(60);
                    e.Result = 0;
                    return;
                }
                else if (response.IsSuccessStatusCode)
                {
                    string strContent = response.Content.ReadAsStringAsync().Result;
                    ZKBData.R2Z2Data r2z2Data = ZKBData.R2Z2Data.FromJson(strContent);

                    if (r2z2Data != null && r2z2Data.Esi != null && r2z2Data.Esi.Victim != null)
                    {
                        ZKBDataSimple zs = new ZKBDataSimple();
                        zs.KillID = r2z2Data.KillmailId;
                        zs.VictimAllianceID = r2z2Data.Esi.Victim.AllianceId;
                        zs.VictimCharacterID = r2z2Data.Esi.Victim.CharacterId;
                        zs.VictimCorpID = r2z2Data.Esi.Victim.CorporationId;
                        zs.SystemName = EveManager.Instance.GetEveSystemNameFromID((int)r2z2Data.Esi.SolarSystemId);
                        zs.KillTime = r2z2Data.Esi.KillmailTime;

                        zs.ShipTypeID = r2z2Data.Esi.Victim.ShipTypeId;
                        string shipID = zs.ShipTypeID.ToString();
                        if(EveManager.Instance.ShipTypes.ContainsKey(shipID))
                        {
                            zs.ShipType = EveManager.Instance.ShipTypes[shipID];
                        }
                        else
                        {
                            zs.ShipType = "Unknown (" + shipID + ")";
                        }

                        zs.VictimAllianceName = EveManager.Instance.GetAllianceName(zs.VictimAllianceID);
                        zs.TotalValue = r2z2Data.Zkb?.TotalValue ?? 0;
                        zs.Hash = r2z2Data.Hash;
                        zs.VictimName = EveManager.Instance.GetCharacterName(zs.VictimCharacterID);

                        // Collect distinct attacker alliance IDs
                        if (r2z2Data.Esi.Attackers != null)
                        {
                            var seen = new HashSet<int>();
                            foreach (var a in r2z2Data.Esi.Attackers)
                            {
                                if (a.AllianceId != 0 && seen.Add(a.AllianceId))
                                    zs.AttackerAllianceIDs.Add(a.AllianceId);
                            }
                        }

                        KillStream.Insert(0, zs);
                        // Sort by actual kill time descending (newest first) —
                        // ZKB API may deliver kills out of order due to latency.
                        KillStream.Sort((a, b) => b.KillTime.CompareTo(a.KillTime));

                        if(KillsAddedEvent != null)
                        {
                            KillsAddedEvent();
                        }
                    }

                    currentSequence++;
                    e.Result = 0;
                }
                else
                {
                    // Any other error, just back off for a bit
                    nextPollTime = DateTime.Now.AddSeconds(10);
                    e.Result = -1;
                }
            }
            catch
            {
                nextPollTime = DateTime.Now.AddSeconds(10);
                e.Result = -1;
            }
        }

        private void zkb_DoWorkComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            bool updatedKillList = false;

            List<int> AllianceIDs = new List<int>();
            List<int> CharacterIDs = new List<int>();
            List<int> CorporationIDs = new List<int>();

            for(int i = KillStream.Count - 1; i >= 0; i--)
            {
                // Resolve attacker alliance IDs
                foreach (var attAllianceID in KillStream[i].AttackerAllianceIDs)
                {
                    if (string.IsNullOrEmpty(EveManager.Instance.GetAllianceTicker(attAllianceID)) &&
                        !AllianceIDs.Contains(attAllianceID))
                    {
                        AllianceIDs.Add(attAllianceID);
                    }
                }
            }

            for(int i = KillStream.Count - 1; i >= 0; i--)
            {
                if(KillStream[i].VictimAllianceName == string.Empty)
                {
                    if(!EveManager.Instance.AllianceIDToTicker.ContainsKey(KillStream[i].VictimAllianceID) && !AllianceIDs.Contains(KillStream[i].VictimAllianceID) && KillStream[i].VictimAllianceID != 0)
                    {
                        AllianceIDs.Add(KillStream[i].VictimAllianceID);
                    }
                    else
                    {
                        KillStream[i].VictimAllianceName = EveManager.Instance.GetAllianceName(KillStream[i].VictimAllianceID);
                    }
                }

                if(string.IsNullOrEmpty(KillStream[i].VictimName) && KillStream[i].VictimCharacterID != 0)
                {
                    string resolved = EveManager.Instance.GetCharacterName(KillStream[i].VictimCharacterID);
                    if(!string.IsNullOrEmpty(resolved))
                    {
                        KillStream[i].VictimName = resolved;
                    }
                    else if(!CharacterIDs.Contains(KillStream[i].VictimCharacterID))
                    {
                        CharacterIDs.Add(KillStream[i].VictimCharacterID);
                    }
                }

                if (KillStream[i].VictimCorpID != 0)
                {
                    string corpName = EveManager.Instance.GetCorporationName(KillStream[i].VictimCorpID);
                    if (!string.IsNullOrEmpty(corpName))
                    {
                        if (KillStream[i].VictimCorpName != corpName)
                            KillStream[i].VictimCorpName = corpName;
                    }
                    else if (!CorporationIDs.Contains(KillStream[i].VictimCorpID))
                    {
                        CorporationIDs.Add(KillStream[i].VictimCorpID);
                    }
                }

                if(KillStream[i].KillTime + TimeSpan.FromMinutes(KillExpireTimeMinutes) < DateTimeOffset.UtcNow)
                {
                    KillStream.RemoveAt(i);

                    updatedKillList = true;
                }
            }
            if(AllianceIDs.Count > 0)
            {
                EveManager.Instance.ResolveAllianceIDs(AllianceIDs);
            }
            if(CharacterIDs.Count > 0)
            {
                _ = EveManager.Instance.ResolveCharacterIDs(CharacterIDs);
            }
            if(CorporationIDs.Count > 0)
            {
                _ = EveManager.Instance.ResolveCorporationIDs(CorporationIDs);
            }

            if(updatedKillList)
            {
                // kills are coming in so fast that this is redundant
                if(KillsAddedEvent != null)
                {
                    KillsAddedEvent();
                }
            }
        }

        /// <summary>
        /// A simple class with the Kill Highlights
        /// </summary>
        public class ZKBDataSimple : INotifyPropertyChanged
        {
            /// <summary>
            /// When true, KillTimeDisplay shows local time instead of UTC.
            /// </summary>
            public static bool DisplayLocalTime { get; set; } = false;

            private string m_victimAllianceName;

            public event PropertyChangedEventHandler PropertyChanged;

            /// <summary>
            /// Gets or sets the ZKillboard Kill ID
            /// </summary>
            public long KillID { get; set; }

            /// <summary>
            /// Gets or sets the killmail hash for in-game linking
            /// </summary>
            public string Hash { get; set; }

            /// <summary>
            /// Gets or sets the victim's character name
            /// </summary>
            public string VictimName
            {
                get => m_victimName;
                set
                {
                    m_victimName = value;
                    OnPropertyChanged(nameof(VictimName));
                }
            }
            private string m_victimName;

            /// <summary>
            /// Gets or sets the time of the kill
            /// </summary>
            public DateTimeOffset KillTime { get; set; }

            /// <summary>
            /// Gets the formatted kill time. Shows local time when DisplayLocalTime is true, otherwise UTC.
            /// </summary>
            public string KillTimeDisplay
            {
                get
                {
                    if (DisplayLocalTime)
                        return KillTime.LocalDateTime.ToString("HH:mm:ss");
                    return KillTime.UtcDateTime.ToString("HH:mm:ss");
                }
            }

            /// <summary>
            /// Gets or sets the Ship Type ID from the kill mail
            /// </summary>
            public int ShipTypeID { get; set; }

            private string m_shipType;

            /// <summary>
            /// Gets or sets the Ship Lost in this kill (English name)
            /// </summary>
            public string ShipType
            {
                get => m_shipType;
                set
                {
                    m_shipType = value;
                    OnPropertyChanged("ShipType");
                    OnPropertyChanged("ShipTypeDisplay");
                }
            }

            /// <summary>
            /// Gets the ship type name in the current display language
            /// </summary>
            public string ShipTypeDisplay
            {
                get
                {
                    if (EveManager.CurrentLanguage == "zh-CN" &&
                        EveManager.Instance != null &&
                        EveManager.Instance.ShipTypesCN != null &&
                        EveManager.Instance.ShipTypesCN.ContainsKey(ShipTypeID.ToString()))
                    {
                        return EveManager.Instance.ShipTypesCN[ShipTypeID.ToString()];
                    }
                    return ShipType;
                }
            }

            /// <summary>
            /// Notify that ShipTypeDisplay may have changed (e.g. after language change or CN names loaded)
            /// </summary>
            public void RefreshShipTypeDisplay()
            {
                OnPropertyChanged("ShipTypeDisplay");
            }

            /// <summary>
            /// Gets or sets the System ID the kill was in
            /// </summary>
            public string SystemName { get; set; }

            /// <summary>
            /// Gets the region name for this kill's system
            /// </summary>
            public string RegionName
            {
                get
                {
                    var sys = EveManager.Instance?.GetEveSystem(SystemName);
                    if (sys == null) return "";
                    if (EveManager.CurrentLanguage == "zh-CN" &&
                        EveManager.Translations.TryGetValue(sys.Region, out var zhRegion))
                        return zhRegion;
                    return sys.Region;
                }
            }

            /// <summary>
            /// Gets or sets the Victims Alliance ID
            /// </summary>
            public int VictimAllianceID { get; set; }

            /// <summary>
            /// Gets or sets the Victims Alliance Name
            /// </summary>
            public string VictimAllianceName
            {
                get
                {
                    return m_victimAllianceName;
                }
                set
                {
                    m_victimAllianceName = value;
                    OnPropertyChanged("VictimAllianceName");
                    OnPropertyChanged("AllianceDisplay");
                }
            }

            /// <summary>
            /// Gets the alliance ticker (abbreviation) for display, falling back to the full name
            /// if the ticker hasn't been resolved yet.
            /// </summary>
            public string AllianceDisplay
            {
                get
                {
                    if (EveManager.Instance != null && VictimAllianceID != 0)
                    {
                        string ticker = EveManager.Instance.GetAllianceTicker(VictimAllianceID);
                        if (!string.IsNullOrEmpty(ticker))
                            return ticker;
                    }
                    return VictimAllianceName;
                }
            }

            /// <summary>
            /// Notify that AllianceDisplay may have changed (e.g. after ticker resolution)
            /// </summary>
            public void RefreshAllianceDisplay()
            {
                OnPropertyChanged("AllianceDisplay");
            }

            /// <summary>
            /// Gets or sets the character ID of the victim
            /// </summary>
            public int VictimCharacterID { get; set; }

            /// <summary>
            /// Gets or sets the Victim's corp ID
            /// </summary>
            public int VictimCorpID { get; set; }

            private string m_victimCorpName;
            /// <summary>
            /// Gets or sets the Victim's corporation name
            /// </summary>
            public string VictimCorpName
            {
                get => m_victimCorpName;
                set
                {
                    m_victimCorpName = value;
                    OnPropertyChanged(nameof(VictimCorpName));
                    OnPropertyChanged(nameof(CorpDisplay));
                }
            }

            /// <summary>
            /// Gets the corporation ticker for display, falling back to the full name
            /// </summary>
            public string CorpDisplay
            {
                get
                {
                    if (EveManager.Instance != null && VictimCorpID != 0)
                    {
                        string ticker = EveManager.Instance.GetCorporationTicker(VictimCorpID);
                        if (!string.IsNullOrEmpty(ticker))
                            return ticker;
                    }
                    return VictimCorpName;
                }
            }

            public void RefreshCorpDisplay()
            {
                OnPropertyChanged(nameof(CorpDisplay));
            }

            /// <summary>
            /// Gets the distinct attacker alliance IDs for this kill
            /// </summary>
            public List<int> AttackerAllianceIDs { get; set; } = new();

            /// <summary>
            /// Gets the distinct attacker alliance tickers for display, separated by "/"
            /// Groups 3 per line with "\n" to prevent excessively wide/tall rows.
            /// </summary>
            public string AttackerAlliancesDisplay
            {
                get
                {
                    var tickers = new List<string>();
                    foreach (var id in AttackerAllianceIDs)
                    {
                        if (EveManager.Instance == null) continue;
                        string t = EveManager.Instance.GetAllianceTicker(id);
                        if (!string.IsNullOrEmpty(t))
                            tickers.Add(t);
                        else
                        {
                            string name = EveManager.Instance.GetAllianceName(id);
                            if (!string.IsNullOrEmpty(name) && !tickers.Contains(name))
                                tickers.Add(name);
                        }
                    }

                    if (tickers.Count == 0) return "";

                    var lines = new List<string>();
                    for (int i = 0; i < tickers.Count; i += 3)
                    {
                        lines.Add(string.Join("/", tickers.Skip(i).Take(3)));
                    }
                    return string.Join("\n", lines);
                }
            }

            /// <summary>
            /// Notify that AttackerAlliancesDisplay may have changed (e.g. after ticker resolution)
            /// </summary>
            public void RefreshAttackerAlliancesDisplay()
            {
                OnPropertyChanged("AttackerAlliancesDisplay");
            }

            /// <summary>
            /// Gets or sets the total ISK value of the destroyed items + fitted items
            /// </summary>
            public double TotalValue { get; set; }

            /// <summary>
            /// Gets the formatted total value string (e.g. "123.4M" or "1.2B")
            /// </summary>
            public string TotalValueDisplay
            {
                get
                {
                    if (TotalValue <= 0) return "-";
                    if (TotalValue >= 1_000_000_000)
                        return $"{TotalValue / 1_000_000_000:F2}B";
                    if (TotalValue >= 1_000_000)
                        return $"{TotalValue / 1_000_000:F2}M";
                    if (TotalValue >= 1_000)
                        return $"{TotalValue / 1_000:F1}K";
                    return TotalValue.ToString("F0");
                }
            }

            public override string ToString()
            {
                string allianceTicker = EVEData.EveManager.Instance.GetAllianceTicker(VictimAllianceID);
                if(allianceTicker == string.Empty)
                {
                    allianceTicker = VictimAllianceID.ToString();
                }

                return string.Format("System: {0}, Alliance: {1}, Ship {2}", SystemName, allianceTicker, ShipType);
            }

            protected void OnPropertyChanged(string name)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if(handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(name));
                }
            }
        }
    }
}
