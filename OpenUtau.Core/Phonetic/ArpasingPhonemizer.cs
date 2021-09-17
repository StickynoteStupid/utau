﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core {
    public class ArpasingPhonemizer : Phonemizer {
        enum PhoneType { vowel, stop, affricate, fricative, aspirate, liquid, nasal, semivowel }
        class TrieNode {
            public Dictionary<char, TrieNode> children = new Dictionary<char, TrieNode>();
            public string[] symbols;
        }

        static readonly object initLock = new object();
        static Dictionary<string, PhoneType> phones;
        static Dictionary<string, string[]> vowelFallback;
        static TrieNode root;

        static void BuildTrie(TrieNode node, string word, int index, string symbols) {
            if (index == word.Length) {
                node.symbols = symbols.Split()
                    .Select(symbol => RemoveTailDigits(symbol))
                    .ToArray();
                return;
            }
            if (!node.children.TryGetValue(word[index], out var child)) {
                child = new TrieNode();
                node.children[word[index]] = child;
            }
            BuildTrie(child, word, index + 1, symbols);
        }

        static string[] GetSymbols(Note note) {
            if (string.IsNullOrEmpty(note.phoneticHint)) {
                return QueryTrie(root, note.lyric, 0);
            }
            return note.phoneticHint.Split()
                .Where(s => phones.ContainsKey(s))
                .ToArray();
        }

        static string[] QueryTrie(TrieNode node, string word, int index) {
            if (index == word.Length) {
                return node.symbols;
            }
            if (node.children.TryGetValue(word[index], out var child)) {
                return QueryTrie(child, word, index + 1);
            }
            return null;
        }

        static string RemoveTailDigits(string s) {
            while (char.IsDigit(s.Last())) {
                s = s.Substring(0, s.Length - 1);
            }
            return s;
        }

        private USinger singer;
        private readonly List<Tuple<int, int>> alignments = new List<Tuple<int, int>>();

        public override string Name => "Arpasing Phonemizer";
        public override string Tag => "EN ARPA";
        public int ConsonantLength { get; set; } = 60;

        public ArpasingPhonemizer() {
            Initialize();
        }

        private static void Initialize() {
            Task.Run(() => {
                try {
                    lock (initLock) {
                        if (ArpasingPhonemizer.root != null) {
                            return;
                        }
                        var root = new TrieNode();
                        phones = Properties.Resources.cmudict_0_7b_phones.Split('\n')
                            .Select(line => line.Trim().ToLowerInvariant())
                            .Select(line => line.Split())
                            .Where(parts => parts.Length == 2)
                            .ToDictionary(parts => parts[0], parts => (PhoneType)Enum.Parse(typeof(PhoneType), parts[1]));
                        vowelFallback = "aa=ah,ae;ae=ah,aa;ah=aa,ae;ao=ow;ow=ao;eh=ae;ih=iy;iy=ih;uh=uw;uw=uh;aw=ao".Split(';')
                            .Select(entry => entry.Split('='))
                            .ToDictionary(parts => parts[0], parts => parts[1].Split(','));
                        Properties.Resources.cmudict_0_7b.Split('\n')
                           .Where(line => !line.StartsWith(";;;"))
                            .Select(line => line.Trim().ToLowerInvariant())
                           .Select(line => line.Split(new string[] { "  " }, StringSplitOptions.None))
                           .Where(parts => parts.Length == 2)
                           .ToList()
                           .ForEach(parts => BuildTrie(root, parts[0], 0, parts[1]));
                        ArpasingPhonemizer.root = root;
                    }
                } catch (Exception e) {
                    Log.Error(e, "Failed to initialize.");
                }
            });
        }

        public override void SetSinger(USinger singer) => this.singer = singer;

        public override Phoneme[] Process(Note[] notes, Note? prevNeighbour, Note? nextNeighbour) {
            lock (initLock) {
                if (root == null) {
                    return new Phoneme[0];
                }
            }
            var note = notes[0];
            var prevSymbols = prevNeighbour == null ? null : GetSymbols(prevNeighbour.Value);
            var symbols = GetSymbols(note);
            if (symbols == null || symbols.Length == 0) {
                if (note.lyric == "-" && prevSymbols != null) {
                    return new Phoneme[] {
                        new Phoneme() {
                            phoneme = $"{prevSymbols.Last()} -",
                        }
                    };
                }
                return new Phoneme[] {
                    new Phoneme() {
                        phoneme = note.lyric,
                    }
                };
            }
            var phoneTypes = symbols.Select(s => phones[s]).ToArray();
            int firstVowel = Array.IndexOf(phoneTypes, PhoneType.vowel);
            var phonemes = new Phoneme[symbols.Length];
            string prevSymbol = prevSymbols == null ? "-" : prevSymbols.Last();
            string phoneme = $"{prevSymbol} {symbols[0]}";
            if (!singer.TryGetMappedOto(phoneme, note.tone, out var _)) {
                phoneme = $"- {symbols[0]}"; // Fallback to not use vc
            }
            phonemes[0] = new Phoneme {
                phoneme = phoneme,
            };
            for (int i = 1; i < symbols.Length; i++) {
                phonemes[i] = new Phoneme {
                    phoneme = GetPhonemeOrFallback(symbols[i - 1], symbols[i], note.tone),
                };
            }

            // Alignments
            alignments.Clear();
            if (firstVowel > 0) {
                alignments.Add(Tuple.Create(firstVowel, 0));
            } else {
                firstVowel = 0;
            }
            int position = 0;
            for (int i = 0; i < notes.Length; ++i) {
                string alignmentHint = notes[i].lyric;
                if (alignmentHint.StartsWith("...")) {
                    alignmentHint = alignmentHint.Substring(3);
                } else {
                    position += notes[i].duration;
                    continue;
                }
                if (int.TryParse(alignmentHint, out int index)) {
                    index--;
                    if (index > 0 && (alignments.Count == 0 || alignments.Last().Item1 < index) && index < phonemes.Length) {
                        alignments.Add(Tuple.Create(index, position));
                    }
                }
                position += notes[i].duration;
            }
            alignments.Add(Tuple.Create(phonemes.Length, position));

            int startIndex = 0;
            int startTick = -ConsonantLength * firstVowel;
            foreach (var alignment in alignments) {
                DistributeDuration(phoneTypes, phonemes, startIndex, alignment.Item1, startTick, alignment.Item2);
                startIndex = alignment.Item1;
                startTick = alignment.Item2;
            }
            alignments.Clear();

            MapPhonemes(notes, phonemes, singer);
            return phonemes;
        }

        string GetPhonemeOrFallback(string prevSymbol, string symbol, int tone) {
            string phoneme = $"{prevSymbol} {symbol}";
            if (singer.TryGetMappedOto(phoneme, tone, out var _)) {
                return phoneme;
            }
            if (vowelFallback.TryGetValue(symbol, out string[] fallbacks)) {
                foreach (var fallback in fallbacks) {
                    phoneme = $"{prevSymbol} {fallback}";
                    if (singer.TryGetMappedOto(phoneme, tone, out var _)) {
                        return phoneme;
                    }
                }
            }
            return $"- {symbol}";
        }

        void DistributeDuration(PhoneType[] phoneTypes, Phoneme[] phonemes, int startIndex, int endIndex, int startTick, int endTick) {
            int consonants = 0;
            int vowels = 0;
            int duration = endTick - startTick;
            for (int i = startIndex; i < endIndex; i++) {
                if (phoneTypes[i] == PhoneType.vowel) {
                    vowels++;
                } else {
                    consonants++;
                }
            }
            int consonantDuration = vowels > 0
                ? (consonants > 0 ? Math.Min(ConsonantLength, duration / 2 / consonants) : 0)
                : duration / consonants;
            int vowelDuration = vowels > 0 ? (duration - consonantDuration * consonants) / vowels : 0;
            int position = startTick;
            for (int i = startIndex; i < endIndex; i++) {
                if (phoneTypes[i] == PhoneType.vowel) {
                    phonemes[i].position = position;
                    position += vowelDuration;
                } else {
                    phonemes[i].position = position;
                    position += consonantDuration;
                }
            }
        }
    }
}
