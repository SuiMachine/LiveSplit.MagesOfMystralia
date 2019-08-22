using System;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.MagesOfMystralia
{
    public partial class MagesOfMystraliaSettings : UserControl
    {
        public bool StartOnFirstLevelLoad { get; set; }
        public bool SplitOnLevelChange { get; set; }
        public int RescansLimit { get; set; }

        //Defaults
        private const bool DEFAULT_AUTOSTART_ONFIRSTLEVELLOAD = false;
        private const bool DEFAULT_SPLIT_ONLEVELCHANGE = true;
        private const int DEFAULT_RESCANS_LIMIT = 0;

        public MagesOfMystraliaSettings()
        {
            InitializeComponent();

            // Bindings
            this.CB_Autostart_on_LevelLoad.DataBindings.Add("Checked", this, "StartOnFirstLevelLoad", false, DataSourceUpdateMode.OnPropertyChanged);
            this.CB_SplitOnLevelChange.DataBindings.Add("Checked", this, "SplitOnLevelChange", false, DataSourceUpdateMode.OnPropertyChanged);
            this.NumUpDn_RescansLimit.DataBindings.Add("Value", this, "RescansLimit", false, DataSourceUpdateMode.OnPropertyChanged);

            // defaults
            this.StartOnFirstLevelLoad = DEFAULT_AUTOSTART_ONFIRSTLEVELLOAD;
            this.SplitOnLevelChange = DEFAULT_SPLIT_ONLEVELCHANGE;
            this.RescansLimit = DEFAULT_RESCANS_LIMIT;
        }

        public XmlNode GetSettings(XmlDocument doc)
        {
            XmlElement settingsNode = doc.CreateElement("Settings");

            settingsNode.AppendChild(ToElement(doc, "Version", Assembly.GetExecutingAssembly().GetName().Version.ToString(3)));

            settingsNode.AppendChild(ToElement(doc, "StartOnFirstLevelLoad", this.StartOnFirstLevelLoad));
            settingsNode.AppendChild(ToElement(doc, "SplitOnLevelChange", this.SplitOnLevelChange));
            settingsNode.AppendChild(ToElement(doc, "RescansLimit", this.RescansLimit));

            return settingsNode;
        }

        public void SetSettings(XmlNode settings)
        {
            this.StartOnFirstLevelLoad = ParseBool(settings, "StartOnFirstLevelLoad", DEFAULT_AUTOSTART_ONFIRSTLEVELLOAD);
            this.SplitOnLevelChange = ParseBool(settings, "SplitOnLevelChange", DEFAULT_SPLIT_ONLEVELCHANGE);
            this.RescansLimit = ParseInt(settings, "RescansLimit", DEFAULT_RESCANS_LIMIT);
        }

        static bool ParseBool(XmlNode settings, string setting, bool default_ = false)
        {
            bool val;
            return settings[setting] != null ?
                (Boolean.TryParse(settings[setting].InnerText, out val) ? val : default_)
                : default_;
        }

        static int ParseInt(XmlNode settings, string setting, int default_ = 0)
        {
            int val;
            return settings[setting] != null ?
                (int.TryParse(settings[setting].InnerText, out val) ? val : default_)
                : default_;
        }

        static XmlElement ToElement<T>(XmlDocument document, string name, T value)
        {
            XmlElement str = document.CreateElement(name);
            str.InnerText = value.ToString();
            return str;
        }
    }
}
