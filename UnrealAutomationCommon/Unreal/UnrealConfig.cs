using System;
using System.Collections.Generic;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class ConfigSection
    {
        private Dictionary<string, string> _values = new();

        public void AddLine(string line)
        {
            if (line.StartsWith("+") || line.StartsWith("-"))
            {
                // Ignore arrays for now
                return;
            }

            string[] split = line.Split(new[] { '=' }, 2);
            _values.Add(split[0], split[1]);
        }

        public string GetValue(string key)
        {
            return _values[key];
        }
    }

    public class UnrealConfig
    {
        private Dictionary<string, ConfigSection> _sections = new();

        public UnrealConfig(string path)
        {
            StreamReader reader = new StreamReader(path);

            ConfigSection currentSection = null;

            while (reader.Peek() >= 0)
            {
                string line = reader.ReadLine();

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (line.StartsWith("["))
                {
                    // New section
                    string sectionName = line.TrimStart('[').TrimEnd(']');

                    currentSection = new ConfigSection();
                    _sections.Add(sectionName, currentSection);
                }
                else
                {
                    if (currentSection == null)
                    {
                        throw new Exception("Text before first section");
                    }

                    currentSection.AddLine(line);
                }
            }
        }

        public ConfigSection GetSection(string name)
        {
            return _sections[name];
        }
    }
}
