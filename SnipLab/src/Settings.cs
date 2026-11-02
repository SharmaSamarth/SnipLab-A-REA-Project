using System;
using System.Collections.Generic;
using System.IO;

namespace SnipLab
{
    // User options, kept in %AppData%\SnipLab\settings.ini as key=value lines.
    internal sealed class Settings
    {
        public bool Record;
        public SnipMode Mode = SnipMode.Rectangle;
        public int Delay;
        public bool AutoCopy = true;
        public bool AutoSave = true;
        public bool CaptureCursor;
        public bool AddBorder;
        public bool RecordSystemAudio = true;
        public bool RecordMicrophone;
        public bool AutoSaveRecordings = true;
        public bool AutoCopyRecordings;

        private static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipLab", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            if (!File.Exists(FilePath)) return settings;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int split = line.IndexOf('=');
                if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }

            string value;
            SnipMode mode;
            int delay;
            if (values.TryGetValue("Mode", out value) && Enum.TryParse(value, out mode)) settings.Mode = mode;
            if (values.TryGetValue("Delay", out value) && int.TryParse(value, out delay)) settings.Delay = delay;
            ReadBool(values, "Record", ref settings.Record);
            ReadBool(values, "AutoCopy", ref settings.AutoCopy);
            ReadBool(values, "AutoSave", ref settings.AutoSave);
            ReadBool(values, "CaptureCursor", ref settings.CaptureCursor);
            ReadBool(values, "AddBorder", ref settings.AddBorder);
            ReadBool(values, "RecordSystemAudio", ref settings.RecordSystemAudio);
            ReadBool(values, "RecordMicrophone", ref settings.RecordMicrophone);
            ReadBool(values, "AutoSaveRecordings", ref settings.AutoSaveRecordings);
            ReadBool(values, "AutoCopyRecordings", ref settings.AutoCopyRecordings);
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, new[]
            {
                "Record=" + Record,
                "Mode=" + Mode,
                "Delay=" + Delay,
                "AutoCopy=" + AutoCopy,
                "AutoSave=" + AutoSave,
                "CaptureCursor=" + CaptureCursor,
                "AddBorder=" + AddBorder,
                "RecordSystemAudio=" + RecordSystemAudio,
                "RecordMicrophone=" + RecordMicrophone,
                "AutoSaveRecordings=" + AutoSaveRecordings,
                "AutoCopyRecordings=" + AutoCopyRecordings
            });
        }

        private static void ReadBool(Dictionary<string, string> values, string key, ref bool target)
        {
            string value;
            bool parsed;
            if (values.TryGetValue(key, out value) && bool.TryParse(value, out parsed)) target = parsed;
        }
    }
}
