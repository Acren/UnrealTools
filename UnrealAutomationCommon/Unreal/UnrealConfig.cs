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

        public void SetValue(string key, string value)
        {
            _values[key] = value;
        }

        public Dictionary<string, string> GetValues()
        {
            return new Dictionary<string, string>(_values);
        }
    }

    public class UnrealConfig
    {
        private readonly Dictionary<string, ConfigSection> _sections = new();
        private readonly string _filePath;
        private readonly List<string> _rawLines = new();
        private readonly Dictionary<int, string> _lineSectionMap = new();

        public UnrealConfig(string path)
        {
            _filePath = path;
            StreamReader reader = new(path);

            ConfigSection currentSection = null;
            string currentSectionName = null;
            int lineIndex = 0;

            while (reader.Peek() >= 0)
            {
                string line = reader.ReadLine();
                _rawLines.Add(line);

                if (!string.IsNullOrEmpty(line))
                {
                    if (line.StartsWith("["))
                    {
                        // New section
                        currentSectionName = line.TrimStart('[').TrimEnd(']');
                        currentSection = new ConfigSection();
                        _sections.Add(currentSectionName, currentSection);
                        _lineSectionMap[lineIndex] = currentSectionName;
                    }
                    else if (currentSection != null && line.Contains('=') && !line.StartsWith("+") && !line.StartsWith("-"))
                    {
                        currentSection.AddLine(line);
                        _lineSectionMap[lineIndex] = currentSectionName;
                    }
                }

                lineIndex++;
            }

            reader.Close();
        }

        public ConfigSection GetSection(string name)
        {
            return _sections.ContainsKey(name) ? _sections[name] : null;
        }

        public void Save()
        {
            List<string> newLines = new();
            HashSet<string> processedSections = new();
            Dictionary<string, HashSet<string>> writtenKeys = new();

            for (int i = 0; i < _rawLines.Count; i++)
            {
                string line = _rawLines[i];
                
                if (string.IsNullOrEmpty(line))
                {
                    newLines.Add(line);
                    continue;
                }

                if (line.StartsWith("["))
                {
                    // Section header
                    newLines.Add(line);
                    string sectionName = line.TrimStart('[').TrimEnd(']');
                    processedSections.Add(sectionName);
                    writtenKeys[sectionName] = new HashSet<string>();
                }
                else if (_lineSectionMap.ContainsKey(i) && line.Contains('=') && !line.StartsWith("+") && !line.StartsWith("-"))
                {
                    // Key-value line
                    string sectionName = _lineSectionMap[i];
                    string key = line.Split(new[] { '=' }, 2)[0].TrimEnd();
                    
                    if (_sections.ContainsKey(sectionName))
                    {
                        string newValue = _sections[sectionName].GetValue(key);
                        if (newValue != null)
                        {
                            newLines.Add($"{key}={newValue}");
                            writtenKeys[sectionName].Add(key);
                        }
                        else
                        {
                            newLines.Add(line);
                        }
                    }
                    else
                    {
                        newLines.Add(line);
                    }
                }
                else
                {
                    newLines.Add(line);
                }
            }

            // Add any new key-value pairs that weren't in the original file
            foreach (var section in _sections)
            {
                if (processedSections.Contains(section.Key))
                {
                    var values = section.Value.GetValues();
                    var existingKeys = writtenKeys.ContainsKey(section.Key) ? writtenKeys[section.Key] : new HashSet<string>();
                    
                    foreach (var kvp in values)
                    {
                        if (!existingKeys.Contains(kvp.Key))
                        {
                            // Find the section in newLines and add the new key-value pair
                            for (int i = 0; i < newLines.Count; i++)
                            {
                                if (newLines[i] == $"[{section.Key}]")
                                {
                                    // Find the end of this section
                                    int insertIndex = i + 1;
                                    while (insertIndex < newLines.Count && !newLines[insertIndex].StartsWith("["))
                                    {
                                        insertIndex++;
                                    }
                                    
                                    // Insert before the next section or at the end
                                    newLines.Insert(insertIndex, $"{kvp.Key}={kvp.Value}");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            File.WriteAllLines(_filePath, newLines);
        }
    }
}