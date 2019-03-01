﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Windows;
using Microsoft.VisualStudio.PlatformUI;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.MoveToNamespace
{
    /// <summary>
    /// Interaction logic for MoveToNamespaceDialog.xaml
    /// </summary>
    internal partial class MoveToNamespaceDialog : DialogWindow
    {
        private readonly MoveToNamespaceDialogViewModel _viewModel;

        public string MoveToNamespaceDialogTitle => ServicesVSResources.Move_to_namespace;
        public string NamespaceLabelText => "Namespace: "; // ServicesVSResources.Namespace_colon;
        public string OK => ServicesVSResources.OK;
        public string Cancel => ServicesVSResources.Cancel;

        internal MoveToNamespaceDialog(MoveToNamespaceDialogViewModel viewModel)
            : base(helpTopic: "vs.csharp.refactoring.movetonamespace")
        {
            _viewModel = viewModel;

            InitializeComponent();
            DataContext = viewModel;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
