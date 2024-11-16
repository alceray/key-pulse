﻿using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace KeyPulse.Views
{
    public partial class ConnectionLogView : UserControl
    {
        public ConnectionLogView()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<ConnectionLogViewModel>();
        }
    }
}
