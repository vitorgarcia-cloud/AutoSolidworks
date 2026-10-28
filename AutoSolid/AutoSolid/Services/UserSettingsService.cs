using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AutoSolid.Services
{
    public class AppSettingsData
    {
        public string SelectedProvider { get; set; } = "Google Gemini";
        public string SelectedModel { get; set; } = "gemini-3.8-flash";
        public bool IsDarkMode { get; set; } = true;
        public Dictionary<string, string> ProviderApiKeys { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serviço responsável por persistir as chaves de API e preferências de tema do usuário em disco (%APPDATA%\AutoSolid\settings.json).
    /// </summary>
    public class UserSettingsService
    {
        private readonly string _settingsFilePath;
        private readonly JavaScriptSerializer _serializer;

        public UserSettingsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "AutoSolid");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _settingsFilePath = Path.Combine(folder, "settings.json");
            _serializer = new JavaScriptSerializer();
        }

        public AppSettingsData Load()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new AppSettingsData();
                }

                string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new AppSettingsData();
                }

                var raw = _serializer.Deserialize<Dictionary<string, object>>(json);
                if (raw == null)
                {
                    return new AppSettingsData();
                }

                var data = new AppSettingsData();

                if (raw.ContainsKey("SelectedProvider") && raw["SelectedProvider"] != null)
                {
                    data.SelectedProvider = raw["SelectedProvider"].ToString();
                }

                if (raw.ContainsKey("SelectedModel") && raw["SelectedModel"] != null)
                {
                    data.SelectedModel = raw["SelectedModel"].ToString();
                }

                if (raw.ContainsKey("IsDarkMode") && raw["IsDarkMode"] != null)
                {
                    if (raw["IsDarkMode"] is bool bDark)
                        data.IsDarkMode = bDark;
                    else if (bool.TryParse(raw["IsDarkMode"].ToString(), out bool parsedDark))
                        data.IsDarkMode = parsedDark;
                }

                if (raw.ContainsKey("ProviderApiKeys") && raw["ProviderApiKeys"] is Dictionary<string, object> keysDict)
                {
                    foreach (var kvp in keysDict)
                    {
                        if (kvp.Value != null)
                        {
                            data.ProviderApiKeys[kvp.Key] = kvp.Value.ToString();
                        }
                    }
                }

                return data;
            }
            catch
            {
                return new AppSettingsData();
            }
        }

        public void Save(AppSettingsData data)
        {
            try
            {
                if (data == null) return;
                string json = _serializer.Serialize(data);
                File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
            }
            catch
            {
                // Ignora falhas de I/O não críticas para não interromper o uso no SolidWorks
            }
        }
    }
}
