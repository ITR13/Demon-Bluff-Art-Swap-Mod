using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArtSwap;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Directory = Il2CppSystem.IO.Directory;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(ArtSwapMod), "ArtSwap", "0.1.0", "ITR")]
[assembly: MelonGame("UmiArt", "Demon Bluff")]

namespace ArtSwap
{
    public class ArtSwapMod : MelonMod
    {
        private CharacterData[] _characterData;
        private int _prevCharacterCount = 0;

        private string _artDirectory;

        public override void OnInitializeMelon()
        {
            _characterData = Array.Empty<CharacterData>();
            _artDirectory = Path.Combine(Application.dataPath, "..", "Mods", "ArtSwap");
            Directory.CreateDirectory(_artDirectory);
        }

        public override void OnLateInitializeMelon()
        {
            Application.runInBackground = true;
        }

        public override void OnUpdate()
        {
            if (_characterData.Length == 0)
            {
                _characterData = Resources.FindObjectsOfTypeAll<CharacterData>();
                return;
            }

            var reload = Input.GetKeyDown(KeyCode.F7);

            var characterCount = _characterData.Length;
            if (characterCount == _prevCharacterCount && !reload) return;
            LoggerInstance.Msg($"Reloading textures");

            _prevCharacterCount = characterCount;

            foreach (var character in _characterData)
            {
                SetupSwap(character);
            }
        }

        private void SetupSwap(CharacterData character)
        {
            if (character == null) return;
            var characterDirectory = Path.Combine(_artDirectory, character.characterId);
            Directory.CreateDirectory(characterDirectory);

            Sprite SwapArt(Sprite sprite, string name)
            {
                var path = Path.Combine(characterDirectory, name + ".png");

                if (File.Exists(path))
                {
                    LoggerInstance.Msg($"Reading {path}");
                    var bytes = File.ReadAllBytes(path);
                    var newTexture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    ImageConversion.LoadImage(newTexture, bytes);

                    newTexture.filterMode = FilterMode.Bilinear;
                    var newSprite = Sprite.Create(
                        newTexture,
                        new Rect(0, 0, newTexture.width, newTexture.height),
                        new(newTexture.width / 2f, newTexture.height / 2f),
                        100
                    );
                    return newSprite;
                }

                if (sprite == null || sprite.texture == null) return sprite;

                {
                    LoggerInstance.Msg($"Creating {path}");
                    var bytes = ReadBytes(sprite.texture);
                    File.WriteAllBytes(path, bytes);
                }
                return sprite;
            }

            character.art = SwapArt(character.art, "art");
            character.art_cute = SwapArt(character.art_cute, "art_cute");
            character.art_nice = SwapArt(character.art_nice, "art_nice");
            character.backgroundArt = SwapArt(character.backgroundArt, "backgroundArt");
            character.randomArt = SwapArt(character.randomArt, "randomArt");

            var colorPath = Path.Combine(characterDirectory, "colors.txt");
            var existingColors = new Dictionary<string, Color>();
            if (File.Exists(colorPath))
            {
                foreach (var colorLine in File.ReadAllLines(colorPath))
                {
                    var split = colorLine.Split(' ');
                    if (split.Length < 2) continue;
                    var colorStr = split[1];
                    if (colorStr[0] != '#') colorStr = "#" + colorStr;
                    if (!ColorUtility.TryParseHtmlString(colorStr, out var color)) continue;
                    existingColors.Add(split[0], color);
                }
            }

            var newColorsFound = false;

            Color SwapColor(Color color, string name)
            {
                if (existingColors.TryGetValue(name, out var newColor)) return newColor;
                existingColors.Add(name, color);
                newColorsFound = true;
                return color;
            }

            character.color = SwapColor(character.color, "color");
            character.cardBgColor = SwapColor(character.cardBgColor, "cardBgColor");
            character.cardBorderColor = SwapColor(character.cardBorderColor, "cardBorderColor");
            character.artBgColor = SwapColor(character.artBgColor, "artBgColor");

            if (!newColorsFound) return;

            var sb = new StringBuilder();
            foreach (var pair in existingColors)
            {
                sb.AppendLine($"{pair.Key} {ColorUtility.ToHtmlStringRGBA(pair.Value)}");
            }

            File.WriteAllText(colorPath, sb.ToString());
        }

        byte[] ReadBytes(Texture2D src)
        {
            var tmp = RenderTexture.GetTemporary(
                src.width,
                src.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            );

            Graphics.Blit(src, tmp);

            // Not really needed, but better safe than sorry
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;

            var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);

            var bytes = ImageConversion.EncodeToPNG(readable);
            Object.Destroy(readable);

            return bytes;
        }
    }
}