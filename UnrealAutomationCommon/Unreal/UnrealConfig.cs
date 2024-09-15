using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public class ConfigSection
    {
        private readonly Dictionary<string, string> _values = new();

        public void AddLine(string line)
        {
            if (line.StartsWith("+") || line.StartsWith("-"))
                // Ignore arrays for now
            {
                return;
            }

            string[] split = line.Split(new[] { '=' }, 2);
            string key = split[0].TrimEnd();

            // Note: Intentionally overwrite existing values
            _values[key] = split[1];
        }

        public string GetValue(string key)
        {
            if (_values.ContainsKey(key))
            {
                return _values[key];
            }

            return null;
        }
    }

    public class UnrealConfig
    {
        private readonly Dictionary<string, ConfigSection> _sections = new();

        public UnrealConfig(string path)
        {
            StreamReader reader = new(path);

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