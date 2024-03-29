﻿using Newtonsoft.Json;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget : INotifyPropertyChanged
    {
        public string Name { get; }
        public string DisplayName { get; }
        public string TargetPath { get; }
        public string TargetDirectory { get; }

        public string OutputDirectory { get; }
        public string TypeName { get; }

        public string TestName { get; set; }

        public bool IsValid { get; }

        // Targets can be nested within each other - e.g. Projects can have Plugins and Packages within them
        public IOperationTarget ParentTarget { get; }

        public IOperationTarget RootTarget
        {
            get
            {
                IOperationTarget currentRoot = this;
                while (true)
                {
                    IOperationTarget parent = currentRoot.ParentTarget;
                    if (parent != null)
                    {
                        currentRoot = parent;
                    }
                    else
                    {
                        break;
                    }
                }

                return currentRoot;
            }
        }

        public bool IsRoot { get; }

        public bool SupportsConfiguration(BuildConfiguration configuration);
    }

    [JsonObject(MemberSerialization.OptIn)]
    public abstract class OperationTarget : IOperationTarget
    {
        [JsonProperty]
        public string TargetPath { get; protected set; }

        private string _testName = string.Empty;
        public abstract string Name { get; }
        public virtual string DisplayName => Name;

        public virtual IOperationTarget ParentTarget => null;

        public string TargetDirectory => TargetPath;
        public string DirectoryName => TargetDirectory != null ? new DirectoryInfo(TargetDirectory).Name : null;

        public bool IsRoot => ParentTarget == null;

        [JsonProperty]
        public string TestName
        {
            get => _testName;
            set
            {
                _testName = value;
                OnPropertyChanged();
            }
        }

        public string OutputDirectory => Path.Combine(OutputPaths.Root(), Name.Replace(" ", ""));
        public string TypeName => GetType().Name.SplitWordsByUppercase();

        public virtual bool IsValid => true;

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return true;
        }

        public abstract void LoadDescriptor();

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public override bool Equals(object other)
        {
            if (other == null || other.GetType() != GetType())
            {
                return false;
            }

            return (other as OperationTarget).TargetPath == TargetPath;
        }
    }
}