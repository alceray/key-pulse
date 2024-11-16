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
    public partial class DeviceListView : UserControl
    {
        public DeviceListView()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<DeviceListViewModel>();
        }
    }
}
