using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace KeyPulse.Helpers
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            } else
            {
                Application.Current.Dispatcher.Invoke(() => 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))
                );
            }
        }
    }
}
