using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ArtSwap;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(ArtSwapMod), "ArtSwap", "0.2.0", "ITR")]
[assembly: MelonGame("UmiArt", "Demon Bluff")]

namespace ArtSwap
{
    public class ArtSwapMod : MelonMod
    {
        private CharacterData[] _characterData;
        private int _prevCharacterCount;
        private string _artDirectory, _skinsDirectory, _inverseDirectory;

        // Threading helpers
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly ConcurrentQueue<string> _logQueue = new();
        private int _totalJobs;
        private int _completedJobs, _completedSprites;
        private float _completionTime = 0f;

        public override void OnInitializeMelon()
        {
            _characterData = Array.Empty<CharacterData>();
            var modsRoot = Path.Combine(Application.dataPath, "..", "Mods");

            _artDirectory = Path.Combine(modsRoot, "ArtSwap");
            _skinsDirectory = Path.Combine(modsRoot, "Skins");
            _inverseDirectory = Path.Combine(_skinsDirectory, "Inverse");

            Directory.CreateDirectory(_artDirectory);
            Directory.CreateDirectory(_skinsDirectory);
            Directory.CreateDirectory(_inverseDirectory);
        }

        public override void OnLateInitializeMelon()
        {
            Application.runInBackground = true;
        }

        public override void OnUpdate()
        {
            if (_totalJobs > 0 && _completedJobs >= _totalJobs && _mainThreadQueue.IsEmpty)
            {
                _completionTime += Time.unscaledDeltaTime;
                if (_completionTime >= 5)
                {
                    _completionTime = 0;
                    _totalJobs = 0;
                    _completedJobs = 0;
                    _completedSprites = 0;
                }
            }

            while (_logQueue.TryDequeue(out var msg)) LoggerInstance.Msg(msg);

            for (var i = 0; i < 5 && _mainThreadQueue.TryDequeue(out var action); i++)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"Main thread task failed: {ex}");
                }
                finally
                {
                    _completedSprites++;
                }
            }

            if (_characterData.Length == 0)
            {
                var characterData = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CharacterData>());
                if (characterData == null)
                {
                    LoggerInstance.BigError("FindObjectsOfTypeAll returned null array");
                    return;
                }

                _characterData = new CharacterData[characterData.Length];
                for (var i = 0; i < _characterData.Length; i++)
                    _characterData[i] = characterData[i]!.Cast<CharacterData>();
                return;
            }

            var reload = Input.GetKeyDown(KeyCode.F7);

            var characterCount = _characterData.Length;
            if (characterCount == _prevCharacterCount && !reload) return;

            LoggerInstance.Msg("Reloading textures (threaded)");
            _prevCharacterCount = characterCount;

            RunSetupThreaded(_characterData);

            UpdateUnlockedSkins();
        }

        private void RunSetupThreaded(CharacterData[] characters)
        {
            _totalJobs = characters.Length;
            _completedJobs = 0;

            Parallel.ForEach(
                characters,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                character =>
                {
                    try
                    {
                        SetupSwapWorker(character);
                    }
                    catch (Exception ex)
                    {
                        _logQueue.Enqueue($"SetupSwap failed: {ex}");
                    }
                    finally
                    {
                        System.Threading.Interlocked.Increment(ref _completedJobs);
                    }
                }
            );
        }

        private void SetupSwapWorker(CharacterData character)
        {
            if (character == null) return;

            var characterDirectory = Path.Combine(_artDirectory, character.characterId);
            Directory.CreateDirectory(characterDirectory);

            var inversePath = Path.Combine(_inverseDirectory, $"{character.characterId}.png");
            if (!File.Exists(inversePath) && character.art_cute?.texture != null)
            {
                try
                {
                    var bytes = ReadBytes(character.art_cute.texture, true);
                    File.WriteAllBytes(inversePath, bytes);
                }
                catch (Exception ex)
                {
                    _logQueue.Enqueue($"Failed to write inverse art: {ex}");
                }
            }

            foreach (var skinPath in Directory.GetFiles(
                         _skinsDirectory,
                         $"{character.characterId}.png",
                         SearchOption.AllDirectories
                     ))
            {
                var skinName = Path.GetFileName(Path.GetDirectoryName(skinPath)!);
                try
                {
                    var bytes = File.ReadAllBytes(skinPath);
                    _mainThreadQueue.Enqueue(() => AssignSkin(character, skinName, bytes));
                }
                catch (Exception ex)
                {
                    _logQueue.Enqueue($"Failed to read skin {skinPath}: {ex}");
                }
            }

            QueueArtSwap(character.art, "art", characterDirectory, s => character.art = s);
            QueueArtSwap(character.art_cute, "art_cute", characterDirectory, s => character.art_cute = s);
            QueueArtSwap(character.art_nice, "art_nice", characterDirectory, s => character.art_nice = s);
            QueueArtSwap(
                character.backgroundArt,
                "backgroundArt",
                characterDirectory,
                s => character.backgroundArt = s
            );
            QueueArtSwap(character.randomArt, "randomArt", characterDirectory, s => character.randomArt = s);

            HandleColors(character, characterDirectory);
        }

        private void QueueArtSwap(Sprite sprite, string name, string dir, Action<Sprite> assign)
        {
            var path = Path.Combine(dir, name + ".png");

            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                _mainThreadQueue.Enqueue(() => assign(CreateSprite(bytes, path)));
            }
            else if (sprite != null && sprite.texture != null)
            {
                try
                {
                    var bytes = ReadBytes(sprite.texture);
                    File.WriteAllBytes(path, bytes);
                    _logQueue.Enqueue($"Exported {path}");
                }
                catch (Exception ex)
                {
                    _logQueue.Enqueue($"Failed to export {name}: {ex}");
                }
            }
        }

        private void AssignSkin(CharacterData character, string skinName, byte[] bytes)
        {
            foreach (var s in character.skins)
            {
                if (s.skinId == skinName)
                {
                    s.art = CreateSprite(bytes, $"{character.characterId}:{skinName}");
                    return;
                }
            }

            var skinData = ScriptableObject.CreateInstance<SkinData>();
            skinData.art = CreateSprite(bytes, $"{character.characterId}:{skinName}");
            skinData.skinId = skinName;
            skinData.name = skinName;
            skinData.skinFor = character;
            skinData.skinRarity = ERarity.Common;
            skinData.artistName = "TODO: Artist Name";
            skinData.artistLink = "TODO: Artist Link";
            skinData.flavor = "TODO: Flavor";
            skinData.notes = "TODO: Notes";
            skinData.unlockWith = new UnlockWith();

            character.skins.Add(skinData);
        }

        private void HandleColors(CharacterData character, string dir)
        {
            var colorPath = Path.Combine(dir, "colors.txt");
            var existingColors = new Dictionary<string, Color>(4);

            if (File.Exists(colorPath))
            {
                foreach (var line in File.ReadAllLines(colorPath))
                {
                    var parts = line.Split(' ');
                    if (parts.Length < 2) continue;

                    var key = parts[0];
                    var colorStr = parts[1];
                    if (colorStr[0] != '#') colorStr = "#" + colorStr;

                    if (ColorUtility.TryParseHtmlString(colorStr, out var c)) existingColors[key] = c;
                }
            }

            var newColorsFound = false;

            Color SwapColor(Color c, string name)
            {
                if (existingColors.TryGetValue(name, out var nc)) return nc;
                existingColors[name] = c;
                newColorsFound = true;
                return c;
            }

            _mainThreadQueue.Enqueue(
                () =>
                {
                    character.color = SwapColor(character.color, "color");
                    character.cardBgColor = SwapColor(character.cardBgColor, "cardBgColor");
                    character.cardBorderColor = SwapColor(character.cardBorderColor, "cardBorderColor");
                    character.artBgColor = SwapColor(character.artBgColor, "artBgColor");

                    if (newColorsFound)
                    {
                        var sb = new StringBuilder();
                        foreach (var pair in existingColors)
                            sb.AppendLine($"{pair.Key} {ColorUtility.ToHtmlStringRGBA(pair.Value)}");

                        File.WriteAllText(colorPath, sb.ToString());
                    }
                }
            );
        }

        private Sprite CreateSprite(byte[] bytes, string source)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                ImageConversion.LoadImage(tex, bytes);
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100);
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to create sprite from {source}: {ex}");
                return null;
            }
        }

        private byte[] ReadBytes(Texture2D src, bool inverse = false)
        {
            var tmp = RenderTexture.GetTemporary(
                src.width,
                src.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            );

            Graphics.Blit(src, tmp);

            var prev = RenderTexture.active;
            RenderTexture.active = tmp;

            var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);

            if (inverse)
            {
                var pixels = readable.GetPixels();
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    Color.RGBToHSV(p, out var h, out var s, out var v);
                    h = (h + 0.5f) % 1f;
                    var nc = Color.HSVToRGB(h, s, v);
                    nc.a = p.a;
                    pixels[i] = nc;
                }

                readable.SetPixels(pixels);
            }

            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);

            var bytes = ImageConversion.EncodeToPNG(readable);
            Object.Destroy(readable);
            return bytes;
        }

        private void UpdateUnlockedSkins()
        {
            var unlockedSkins = SavesGame.UnlockedSkins;
            var ids = unlockedSkins.ids;
            var seenSkins = new HashSet<string>(ids.Count);
            foreach (var skin in ids) seenSkins.Add(skin);

            var anyChange = false;
            foreach (var dir in Directory.GetDirectories(_skinsDirectory, "*", SearchOption.AllDirectories))
            {
                var skinName = Path.GetFileName(dir);
                if (seenSkins.Add(skinName))
                {
                    ids.Add(skinName);
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                SavesGame.UnlockedSkins = unlockedSkins;
                PlayerPrefs.Save();
            }
        }

        public override void OnGUI()
        {
            if (_totalJobs <= 0) return;

            float progress = (float)(_completedJobs + _completedSprites) /
                             (_totalJobs + _completedSprites + _mainThreadQueue.Count);
            GUI.Box(new Rect(10, 10, 300, 25), $"Loading skins: {progress:P0}");
            GUI.Box(new Rect(10, 40, (int)(300 * progress), 10), "");
        }
    }
}