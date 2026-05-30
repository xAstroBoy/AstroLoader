using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Drawing;
using MelonLoader.Utils;
using UnityVersion = AssetRipper.VersionUtilities.UnityVersion;

namespace MelonLoader.InternalUtils
{
    public static class UnityInformationHandler
    {
        private const string DefaultInfo = "UNKNOWN";

        public static string GameName { get; private set; }
        public static string GameDeveloper { get; private set; }
        public static UnityVersion EngineVersion { get; private set; }
        public static string GameVersion { get; private set; }

        // Matches the canonical Unity engine version token: <major>.<minor>.<patch><type><build>
        // (type is one of a=alpha, b=beta, c=china, f=final, p=patch, x=experimental).
        private static readonly Regex UnityVersionToken = new Regex(@"\d+\.\d+\.\d+[abcfpx]\d+", RegexOptions.Compiled);

        private static UnityVersion TryParse(string version)
        {
            if (string.IsNullOrEmpty(version))
                return UnityVersion.MinVersion;

            string cleaned = version.Trim();

            // Unity serialized files / AssetBundle headers frequently append a build or
            // changeset suffix to the engine version, e.g. "2022.3.45f1-378343" or
            // "2022.3.45f1_abcdef123456". AssetRipper's UnityVersion.Parse only accepts the
            // canonical form and throws on anything else, which left EngineVersion at
            // MinVersion (0.0.0a0). Extract the canonical token first so detection is
            // universal across modern Unity versions.
            Match match = UnityVersionToken.Match(cleaned);
            if (match.Success)
                cleaned = match.Value;

            UnityVersion returnval = UnityVersion.MinVersion;
            try
            {
                returnval = UnityVersion.Parse(cleaned);
            }
            catch (Exception ex)
            {
                if (MelonDebug.IsEnabled())
                    MelonLogger.Error(ex);
                returnval = UnityVersion.MinVersion;
            }
            return returnval;
        }

        internal static void Setup()
        {
            string gameDataPath = MelonEnvironment.UnityGameDataDirectory;

            if (!string.IsNullOrEmpty(MelonLaunchOptions.Core.UnityVersion))
                EngineVersion = TryParse(MelonLaunchOptions.Core.UnityVersion);

            AssetsManager assetsManager = new AssetsManager();
            ReadGameInfo(assetsManager, gameDataPath);
            assetsManager.UnloadAll();

            if (string.IsNullOrEmpty(GameDeveloper)
                || string.IsNullOrEmpty(GameName))
                ReadGameInfoFallback();

            if (EngineVersion == UnityVersion.MinVersion)
                EngineVersion = ReadVersionFallback(gameDataPath);

            if (string.IsNullOrEmpty(GameDeveloper))
                GameDeveloper = DefaultInfo;
            if (string.IsNullOrEmpty(GameName))
                GameName = DefaultInfo;
            if (string.IsNullOrEmpty(GameVersion))
                GameVersion = DefaultInfo;

            MelonLogger.WriteLine(Color.Magenta);
            MelonLogger.Msg($"Game Name: {GameName}");
            MelonLogger.Msg($"Game Developer: {GameDeveloper}");
            MelonLogger.Msg($"Unity Version: {EngineVersion}");
            MelonLogger.Msg($"Game Version: {GameVersion}");
            MelonLogger.WriteLine(Color.Magenta);
            MelonLogger.WriteSpacer();
        }

        private static void ReadGameInfo(AssetsManager assetsManager, string gameDataPath)
        {
            AssetsFileInstance instance = null;
            try
            {
                string bundlePath = Path.Combine(gameDataPath, "globalgamemanagers");
                if (!APKAssetManager.DoesAssetExist(bundlePath))
                    bundlePath = Path.Combine(gameDataPath, "mainData");

                if (!APKAssetManager.DoesAssetExist(bundlePath))
                {
                    bundlePath = Path.Combine(gameDataPath, "data.unity3d");
                    if (!APKAssetManager.DoesAssetExist(bundlePath))
                        return;

                    // AssetsTools.NET needs random access while reading/unpacking the bundle,
                    // but the Android AssetManager stream (APKAssetStream) is forward-only and
                    // throws on seek. Buffer the asset into a seekable MemoryStream first.
                    Stream bundleStream = new MemoryStream(APKAssetManager.GetAssetBytes(bundlePath));
                    BundleFileInstance bundleFile = assetsManager.LoadBundleFile(bundleStream, bundlePath);

                    // NOTE: We deliberately avoid AssetsManager.LoadAssetsFileFromBundle here.
                    // That helper gates on AssetBundleFile.IsAssetsFile(), whose heuristic in the
                    // bundled AssetsTools.NET version returns a false negative for SerializedFile
                    // format >= 0x16 (Unity 2017+/2020+/2022+), so it returns null and game info is
                    // never read. Instead we locate the serialized file, read its (already
                    // decompressed) bytes from the bundle and load it as a standalone assets file,
                    // which parses every modern format correctly.
                    instance = LoadSerializedFileFromBundle(assetsManager, bundleFile, "globalgamemanagers");
                }
                else
                {
                    Stream bundleStream = new MemoryStream(APKAssetManager.GetAssetBytes(bundlePath));
                    instance = assetsManager.LoadAssetsFile(bundleStream, bundlePath, true);
                }

                if (instance == null)
                    return;

                assetsManager.LoadIncludedClassPackage();
                if (!instance.file.Metadata.TypeTreeEnabled)
                    assetsManager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

                if (EngineVersion == UnityVersion.MinVersion)
                    EngineVersion = TryParse(instance.file.Metadata.UnityVersion);

                List<AssetFileInfo> assetFiles = instance.file.GetAssetsOfType(AssetClassID.PlayerSettings);
                if (assetFiles.Count > 0)
                {
                    AssetFileInfo playerSettings = assetFiles.First();

                    AssetTypeValueField playerSettings_baseField = assetsManager.GetBaseField(instance, playerSettings);
                    if (playerSettings_baseField != null)
                    {
                        AssetTypeValueField bundleVersion = playerSettings_baseField.Get("bundleVersion");
                        if (bundleVersion != null)
                            GameVersion = bundleVersion.AsString;

                        AssetTypeValueField companyName = playerSettings_baseField.Get("companyName");
                        if (companyName != null)
                            GameDeveloper = companyName.AsString;

                        AssetTypeValueField productName = playerSettings_baseField.Get("productName");
                        if (productName != null)
                            GameName = productName.AsString;
                    }
                }
            }
            catch (Exception ex)
            {
                if (MelonDebug.IsEnabled())
                    MelonLogger.Error(ex);
            }
            if (instance != null)
                instance.file.Close();
        }

        // Loads a serialized file (e.g. "globalgamemanagers") out of an already-loaded bundle
        // without going through AssetsManager.LoadAssetsFileFromBundle, which can wrongly reject
        // newer SerializedFile formats via its IsAssetsFile() heuristic. Returns null if the file
        // isn't present in the bundle.
        private static AssetsFileInstance LoadSerializedFileFromBundle(AssetsManager assetsManager, BundleFileInstance bundleFile, string name)
        {
            int index = bundleFile.file.GetFileIndex(name);
            if (index < 0)
                return null;

            bundleFile.file.GetFileRange(index, out long offset, out long length);

            AssetsFileReader reader = bundleFile.file.DataReader;
            reader.Position = offset;
            byte[] serializedFileData = reader.ReadBytes((int)length);

            return assetsManager.LoadAssetsFile(new MemoryStream(serializedFileData), name, false);
        }

        private static void ReadGameInfoFallback()
        {
            // i don't think any android apps have app.info and i don't know any other way to get game info (unless i just parse the package name, but that's kinda dumb)
            /*try
            {
                string appInfoFilePath = Path.Combine(MelonEnvironment.UnityGameDataDirectory, "app.info");
                if (!File.Exists(appInfoFilePath))
                    return;

                string[] filestr = File.ReadAllLines(appInfoFilePath);
                if ((filestr == null) || (filestr.Length < 2))
                    return;

                if (string.IsNullOrEmpty(GameDeveloper) && !string.IsNullOrEmpty(filestr[0]))
                    GameDeveloper = filestr[0];

                if (string.IsNullOrEmpty(GameName) && !string.IsNullOrEmpty(filestr[1]))
                    GameName = filestr[1];

            }
            catch (Exception ex)
            {
                if (MelonDebug.IsEnabled())
                    MelonLogger.Error(ex);
            }*/
        }

        private static UnityVersion ReadVersionFallback(string gameDataPath)
        {
            try
            {
                var globalgamemanagersPath = Path.Combine(gameDataPath, "globalgamemanagers");
                if (APKAssetManager.DoesAssetExist(globalgamemanagersPath))
                    return GetVersionFromGlobalGameManagers(APKAssetManager.GetAssetBytes(globalgamemanagersPath));
            }
            catch (Exception ex)
            {
                if (MelonDebug.IsEnabled())
                    MelonLogger.Error(ex);
            }

            try
            {
                var dataPath = Path.Combine(gameDataPath, "data.unity3d");
                if (APKAssetManager.DoesAssetExist(dataPath))
                    return GetVersionFromDataUnity3D(APKAssetManager.GetAssetStream(dataPath));
            }
            catch (Exception ex)
            {
                if (MelonDebug.IsEnabled())
                    MelonLogger.Error(ex);
            }

            return UnityVersion.MinVersion;
        }

        private static UnityVersion GetVersionFromGlobalGameManagers(byte[] ggmBytes)
        {
            var verString = new StringBuilder();
            var idx = 0x14;
            while (ggmBytes[idx] != 0)
            {
                verString.Append(Convert.ToChar(ggmBytes[idx]));
                idx++;
            }

            Regex UnityVersionRegex = new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+[abcfx][0-9]+$", RegexOptions.Compiled);
            string unityVer = verString.ToString();
            if (!UnityVersionRegex.IsMatch(unityVer))
            {
                idx = 0x30;
                verString = new StringBuilder();
                while (ggmBytes[idx] != 0)
                {
                    verString.Append(Convert.ToChar(ggmBytes[idx]));
                    idx++;
                }

                unityVer = verString.ToString().Trim();
            }

            return TryParse(unityVer);
        }

        private static UnityVersion GetVersionFromDataUnity3D(Stream fileStream)
        {
            var verString = new StringBuilder();

            if (fileStream.CanSeek)
                fileStream.Seek(0x12, SeekOrigin.Begin);
            else
            {
                if (fileStream.Read(new byte[0x12], 0, 0x12) != 0x12)
                    throw new("Failed to seek to 0x12 in data.unity3d");
            }

            while (true)
            {
                var read = fileStream.ReadByte();
                if (read == 0)
                    break;
                verString.Append(Convert.ToChar(read));
            }

            return TryParse(verString.ToString().Trim());
        }
    }
}
