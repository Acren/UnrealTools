﻿using System;
using System.Collections.Generic;
using System.Windows;
using UnrealAutomationCommon.Operations.OperationTypes;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for OperationOptionsControl.xaml
    /// </summary>
    public partial class OperationOptionsControl : OptionsUserControl
    {
        public static readonly DependencyProperty SelectedOperationTypeProperty = DependencyProperty.Register(nameof(SelectedOperationType), typeof(Type), typeof(OperationOptionsControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public Type SelectedOperationType
        {
            get => (Type)GetValue(SelectedOperationTypeProperty);
            set
            {
                SetValue(SelectedOperationTypeProperty, value);
                OnPropertyChanged();
            }
        }

        public List<Type> OperationTypes => OperationList.GetOrderedOperationTypes();

        public OperationOptionsControl()
        {
            InitializeComponent();
        }
    }
}