using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArtSwap;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(ArtSwapMod), "ArtSwap", "0.1.1", "ITR")]
[assembly: MelonGame("UmiArt", "Demon Bluff")]

namespace ArtSwap
{
    public class ArtSwapMod : MelonMod
    {
        private CharacterData[] _characterData;
        private int _prevCharacterCount;
        private string _artDirectory, _skinsDirectory, _inverseDirectory;

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

            LoggerInstance.Msg("Reloading textures");
            _prevCharacterCount = characterCount;

            foreach (var character in _characterData) SetupSwap(character);

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

        private void SetupSwap(CharacterData character)
        {
            if (character == null) return;

            var characterDirectory = Path.Combine(_artDirectory, character.characterId);
            Directory.CreateDirectory(characterDirectory);

            var inversePath = Path.Combine(_inverseDirectory, $"{character.characterId}.png");
            if (!File.Exists(inversePath) && character.art_cute != null && character.art_cute.texture != null)
            {
                try
                {
                    var bytes = ReadBytes(character.art_cute.texture, true);
                    File.WriteAllBytes(inversePath, bytes);
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"Failed to write inverse art: {ex}");
                }
            }

            var skins = Directory.GetFiles(
                _skinsDirectory,
                $"{character.characterId}.png",
                SearchOption.AllDirectories
            );
            foreach (var skinPath in skins)
            {
                var skinName = Path.GetFileName(Path.GetDirectoryName(skinPath)!);

                var exists = false;
                foreach (var s in character.skins)
                {
                    if (s.skinId == skinName)
                    {
                        s.art = CreateSprite(skinPath);
                        exists = true;
                        break;
                    }
                }

                if (exists) continue;

                var skinData = ScriptableObject.CreateInstance<SkinData>();
                skinData.art = CreateSprite(skinPath);
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

            character.art = SwapArt(character.art, "art", characterDirectory);
            character.art_cute = SwapArt(character.art_cute, "art_cute", characterDirectory);
            character.art_nice = SwapArt(character.art_nice, "art_nice", characterDirectory);
            character.backgroundArt = SwapArt(character.backgroundArt, "backgroundArt", characterDirectory);
            character.randomArt = SwapArt(character.randomArt, "randomArt", characterDirectory);

            var colorPath = Path.Combine(characterDirectory, "colors.txt");
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

        private Sprite SwapArt(Sprite sprite, string name, string dir)
        {
            var path = Path.Combine(dir, name + ".png");

            if (File.Exists(path)) return CreateSprite(path);

            if (sprite == null || sprite.texture == null) return sprite;

            try
            {
                LoggerInstance.Msg($"Creating {path}");
                var bytes = ReadBytes(sprite.texture);
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to export {name}: {ex}");
            }

            return sprite;
        }

        private Sprite CreateSprite(string path)
        {
            LoggerInstance.Msg($"Reading {path}");
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            ImageConversion.LoadImage(tex, bytes);
            tex.filterMode = FilterMode.Bilinear;

            return Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100
            );
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
                    float h, s, v;
                    Color.RGBToHSV(p, out h, out s, out v);
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
    }
}