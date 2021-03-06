﻿using Zenject;
using Polyglot;
using System.IO;
using UnityEngine;
using System.Linq;
using IPA.Utilities;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using HarmonyLib;
using System.Reflection.Emit;
using System.Reflection;

namespace SiraUtil
{
    [HarmonyPatch(typeof(LocalizationImporter), "ImportTextFile")]
    internal static class RemoveLocalizationLog
    {
        private static readonly List<OpCode> _logOpCodes = new List<OpCode>()
        {
             OpCodes.Ldstr,
             OpCodes.Ldloc_S,
             OpCodes.Ldstr,
             OpCodes.Call,
             OpCodes.Call
        };

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (Utilities.OpCodeSequence(codes, _logOpCodes, i))
                {
                    codes.RemoveRange(i, _logOpCodes.Count);
                    break;
                }
            }

            return codes.AsEnumerable();
        }
    }

    public class Localizer : IInitializable
    {
        private static readonly Dictionary<string, LocalizationAsset> _lockedAssetCache = new Dictionary<string, LocalizationAsset>();

        private readonly Config _config;
        private readonly WebClient _webClient;

        public Localizer(Config config, WebClient webClient)
        {
            _config = config;
            _webClient = webClient;
        }

        public async void Initialize()
        {
#if DEBUG
            Stopwatch stopwatch = Stopwatch.StartNew();
#endif
            int successCount = 0;
            foreach (var source in _config.Localization.Sources.Where(s => s.Value.Enabled == true))
            {
                WebResponse response = await _webClient.GetAsync(source.Value.URL, CancellationToken.None);
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        if (!_lockedAssetCache.TryGetValue(source.Key, out LocalizationAsset asset))
                        {
                            using (MemoryStream ms = new MemoryStream(response.ContentToBytes()))
                            {
                                using (StreamReader reader = new StreamReader(ms))
                                {
                                    asset = new LocalizationAsset
                                    {
                                        Format = source.Value.Format,
                                        TextAsset = new TextAsset(response.ContentToString())
                                    };
                                }
                            }
                            _lockedAssetCache.Add(source.Key, asset);
                            Localization.Instance.GetField<List<LocalizationAsset>, Localization>("inputFiles").Add(asset);
                            LocalizationImporter.Refresh();
                        }
                        successCount++;
                    }
                    catch
                    {
                        Plugin.Log.Warn($"Could not parse localization data from {source.Key}");
                        continue;
                    }
                }
                else
                {
                    Plugin.Log.Warn($"Could not fetch localization data from {source.Key}");
                }
            }
#if DEBUG
            stopwatch.Stop();
            Plugin.Log.Info($"Took {stopwatch.Elapsed.TotalSeconds} seconds to download, parse, and load {successCount} localization sheets.");
#endif
            CheckLanguages();
            /*List<string> keys = LocalizationImporter.GetKeys();

            string savePath = Path.Combine(UnityGame.UserDataPath, "SiraUtil", "Localization", "Dumps");
            if (!Directory.Exists(savePath))
            {
                Directory.CreateDirectory(savePath);
            }
            File.WriteAllLines(Path.Combine(savePath, "Keys.txt"), keys.ToArray());*/
            /*
            List<string> english = new List<string>();
            foreach (var key in keys)
            {
                var contains = LocalizationImporter.GetLanguagesContains(key);
                english.Add(contains[key].First());
            }
            File.WriteAllLines(Path.Combine(savePath, "English.txt"), english.ToArray());
            */
            /*
            Localization.Instance.GetField<List<Language>, Localization>("supportedLanguages").Add(Language.French);
            Localization.Instance.SelectedLanguage = Language.French;*/
        }

        public void CheckLanguages()
        {
#if DEBUG
            Stopwatch stopwatch = Stopwatch.StartNew();
#endif
            var supported = Localization.Instance.GetField<List<Language>, Localization>("supportedLanguages");
            FieldInfo field = typeof(LocalizationImporter).GetField("languageStrings", BindingFlags.NonPublic | BindingFlags.Static);
            var locTable = (Dictionary<string, List<string>>)field.GetValue(null);
            ISet<int> validLanguages = new HashSet<int>();
            foreach (var value in locTable.Values)
            {
                for (int i = 0; i < value.Count; i++)
                {
                    if (!string.IsNullOrEmpty(value.ElementAtOrDefault(i)))
                    {
                        validLanguages.Add(i);
                    }
                }
            }

            supported.Clear();
            for (int i = 0; i < validLanguages.Count; i++)
            {
                supported.Add((Language)validLanguages.ElementAt(i));
#if DEBUG
                Plugin.Log.Info($"Language Detected: {(Language)validLanguages.ElementAt(i)}");
#endif
            }
#if DEBUG
            stopwatch.Stop();
            Plugin.Log.Info($"Took {stopwatch.Elapsed:c} to recalculate languages.");
#endif

        }

        public void AddLocalizationSheet(LocalizationAsset localizationAsset)
        {
            var loc = _lockedAssetCache.Where(x => x.Value == localizationAsset || x.Value.TextAsset.text == localizationAsset.TextAsset.text).FirstOrDefault();
            if (loc.Equals(default(KeyValuePair<string, LocalizationAsset>)))
                return;
            Localization.Instance.GetField<List<LocalizationAsset>, Localization>("inputFiles").Add(localizationAsset);
            LocalizationImporter.Refresh();
        }

        public void RemoveLocalizationSheet(LocalizationAsset localizationAsset)
        {
            var loc = _lockedAssetCache.Where(x => x.Value == localizationAsset || x.Value.TextAsset.text == localizationAsset.TextAsset.text).FirstOrDefault();
            if (!loc.Equals(default(KeyValuePair<string, LocalizationAsset>)))
            {
                _lockedAssetCache.Remove(loc.Key);
            }
        }

        public void RemoveLocalizationSheet(string key)
        {
            _lockedAssetCache.Remove(key);
        }

        public LocalizationAsset AddLocalizationSheet(string localizationAsset, GoogleDriveDownloadFormat type, string id, bool addToPolyglot = true)
        {
            LocalizationAsset asset = new LocalizationAsset
            {
                Format = type,
                TextAsset = new TextAsset(localizationAsset)
            };
            if (!_lockedAssetCache.ContainsKey(id))
                _lockedAssetCache.Add(id, asset);
            if (addToPolyglot)
            {
                AddLocalizationSheet(asset);
            }
            return asset;
        }

        public LocalizationAsset AddLocalizationSheetFromAssembly(string assemblyPath, GoogleDriveDownloadFormat type, bool addToPolyglot = true)
        {
            Utilities.AssemblyFromPath(assemblyPath, out Assembly assembly, out string path);
            string content = Utilities.GetResourceContent(assembly, path);
            var locSheet = AddLocalizationSheet(content, type, path, addToPolyglot);
            if (!_lockedAssetCache.ContainsKey(path))
                _lockedAssetCache.Add(path, locSheet);
            return locSheet;
        }
    }
}