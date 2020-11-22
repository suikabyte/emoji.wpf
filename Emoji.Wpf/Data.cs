﻿//
//  Emoji.Wpf — Emoji support for WPF
//
//  Copyright © 2017—2020 Sam Hocevar <sam@hocevar.net>
//
//  This library is free software. It comes without any warranty, to
//  the extent permitted by applicable law. You can redistribute it
//  and/or modify it under the terms of the Do What the Fuck You Want
//  to Public License, Version 2, as published by the WTFPL Task Force.
//  See http://www.wtfpl.net/ for more details.
//

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Emoji.Wpf
{
    public static class EmojiData
    {
        public static EmojiTypeface Typeface { get; private set; }

        public static IEnumerable<Emoji> AllEmoji
        {
            get
            {
                foreach (var group in AllGroups)
                    foreach (var emoji in group.EmojiList)
                        yield return emoji;
            }
        }

        public static IList<Group> AllGroups { get; private set; }

        public static IDictionary<string, Emoji> Lookup { get; private set; }

        public static Regex MatchOne { get; private set; }
        public static Regex MatchMultiple { get; private set; }

        // FIXME: should we lazy load this? If the user calls Load() later, then
        // this first Load() call will have been for nothing.
        static EmojiData() => Load(null);

        public static void Load(string font_name = null)
        {
            Typeface = new EmojiTypeface(font_name);
            ParseEmojiList();
        }

        public class Emoji
        {
            public string Name { get; set; }
            public string Text { get; set; }
            public bool Renderable { get; set; }

            public Group Group => SubGroup.Group;
            public SubGroup SubGroup;

            public IList<Emoji> VariationList { get; } = new List<Emoji>();
        }

        public class SubGroup
        {
            public string Name { get; set; }
            public Group Group;

            public IList<Emoji> EmojiList { get; } = new List<Emoji>();
        }

        public class Group
        {
            public string Name { get; set; }
            public string Icon => SubGroups[0].EmojiList[0].Text;

            public IList<SubGroup> SubGroups { get; } = new List<SubGroup>();

            public int EmojiCount
            {
                get
                {
                    int i = 0;
                    foreach (var subgroup in SubGroups)
                        i += subgroup.EmojiList.Count;
                    return i;
                }
            }

            public IEnumerable<Emoji> EmojiList
            {
                get
                {
                    foreach (var subgroup in SubGroups)
                        foreach (var emoji in subgroup.EmojiList)
                            yield return emoji;
                }
            }
        }

        // FIXME: this could be read directly from emoji-test.txt.gz
        private static List<string> SkinToneComponents = new List<string>()
        {
            "🏻", // light skin tone
            "🏼", // medium-light skin tone
            "🏽", // medium skin tone
            "🏾", // medium-dark skin tone
            "🏿", // dark skin tone
        };

        private static List<string> HairStyleComponents = new List<string>()
        {
            "🦰", // red hair
            "🦱", // curly hair
            "🦳", // white hair
            "🦲", // bald
        };

        private static void ParseEmojiList()
        {
            var match_group = new Regex(@"^# group: (.*)");
            var match_subgroup = new Regex(@"^# subgroup: (.*)");
            var match_sequence = new Regex(@"^([0-9a-fA-F ]+[0-9a-fA-F]).*; *([-a-z]*) *# [^ ]* (E[0-9.]* )?(.*)");
            var match_skin_tone = new Regex($"({string.Join("|", SkinToneComponents.ToArray())})");
            var match_hair_style = new Regex($"({string.Join("|", HairStyleComponents.ToArray())})");
            var list = new List<Group>();
            var text_lookup = new Dictionary<string, Emoji>();
            var name_lookup = new Dictionary<string, Emoji>();
            var alltext = new List<string>();

            Group current_group = null;
            SubGroup current_subgroup = null;

            foreach (var line in EmojiDescriptionLines())
            {
                var m = match_group.Match(line);
                if (m.Success)
                {
                    current_group = new Group() { Name = m.Groups[1].ToString() };
                    list.Add(current_group);
                    continue;
                }

                m = match_subgroup.Match(line);
                if (m.Success)
                {
                    current_subgroup = new SubGroup() { Name = m.Groups[1].ToString(), Group = current_group };
                    current_group.SubGroups.Add(current_subgroup);
                    continue;
                }

                m = match_sequence.Match(line);
                if (m.Success)
                {
                    string sequence = m.Groups[1].ToString();
                    string name = m.Groups[4].ToString();

                    string text = "";
                    foreach (var item in sequence.Split(' '))
                    {
                        int codepoint = Convert.ToInt32(item, 16);
                        text += char.ConvertFromUtf32(codepoint);
                    }

                    // Construct a regex to replace e.g. "🏻" with "(🏻|🏼|🏽|🏾|🏿)" in a big
                    // regex so that we can match all variations of this Emoji even if they are
                    // not in the standard.
                    bool has_modifier = false;
                    bool has_nonfirst_modifier = false;
                    var regex_text = match_skin_tone.Replace(
                        match_hair_style.Replace(text, (x) =>
                        {
                            has_modifier = true;
                            has_nonfirst_modifier |= x.Value != HairStyleComponents[0];
                            return match_hair_style.ToString();
                        }), (x) =>
                        {
                            has_modifier = true;
                            has_nonfirst_modifier |= x.Value != SkinToneComponents[0];
                            return match_skin_tone.ToString();
                        });

                    if (!has_nonfirst_modifier)
                        alltext.Add(has_modifier ? regex_text : text);

                    // Only add fully-qualified characters to the groups, or we will
                    // end with a lot of dupes.
                    if (line.Contains("unqualified") || line.Contains("minimally-qualified"))
                    {
                        // Skip this if there is already a fully qualified version
                        if (text_lookup.ContainsKey(text + "\ufe0f"))
                            continue;
                        if (text_lookup.ContainsKey(text.Replace("\u20e3", "\ufe0f\u20e3")))
                            continue;
                    }

                    var emoji = new Emoji()
                    {
                        Name = name,
                        Text = text,
                        SubGroup = current_subgroup,
                        Renderable = Typeface.CanRender(text),
                    };
                    text_lookup[text] = emoji;
                    name_lookup[name] = emoji;

                    // Get the left part of the name and check whether we’re a variation of an existing
                    // emoji. If so, append to that emoji. Otherwise, add to current subgroup.
                    // FIXME: does not work properly because variations can appear before the generic emoji
                    if (has_modifier && name_lookup.TryGetValue(name.Split(':')[0], out var parent_emoji))
                        parent_emoji.VariationList.Add(emoji);
                    else
                        current_subgroup.EmojiList.Add(emoji);
                }
            }

            // Remove empty groups, for instance the Components
            for (int i = list.Count; --i > 0;)
                if (list[i].EmojiCount == 0)
                    list.RemoveAt(i);

            AllGroups = list;
            Lookup = text_lookup;

            // Build a regex that matches any Emoji
            var textarray = alltext.ToArray();
            Array.Sort(textarray, (a, b) => b.Length - a.Length);
            var regextext = "(" + string.Join("|", textarray).Replace("*", "[*]") + ")";
            MatchOne = new Regex(regextext);
            MatchMultiple = new Regex(regextext + "+");
        }

        private static IEnumerable<string> EmojiDescriptionLines()
        {
            using (var sr = new CompressedResourceStream("emoji-test.txt.gz"))
            {
                foreach (var line in sr.ReadToEnd().Split('\r', '\n'))
                {
                    yield return line;

                    // Append these extra Microsoft emojis after 😾 E2.0 pouting cat
                    if (line.StartsWith("1F63E  "))
                    {
                        yield return "1F431 200D 1F3CD ; fully-qualified # 🐱‍🏍 stunt cat";
                        yield return "1F431 200D 1F453 ; fully-qualified # 🐱‍👓 hipster cat";
                        yield return "1F431 200D 1F680 ; fully-qualified # 🐱‍🚀 astro cat";
                        yield return "1F431 200D 1F464 ; fully-qualified # 🐱‍👤 ninja cat";
                        yield return "1F431 200D 1F409 ; fully-qualified # 🐱‍🐉 dino cat";
                        yield return "1F431 200D 1F4BB ; fully-qualified # 🐱‍💻 hacker cat";
                    }
                }
            }
        }
    }
}

